using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityDiffTool
{
    class Program
    {
        static StringBuilder consoleLog = new StringBuilder();

        static void Log(string message)
        {
            Console.WriteLine(message);
            consoleLog.AppendLine(message);
        }

        static void Main(string[] args)
        {
            if (args.Length != 3)
            {
                Log("Usage: UnityDiffTool <old_game_src> <new_game_src> <mod_src>");
                return;
            }

            string oldSrcFolder = args[0];
            string newSrcFolder = args[1];
            string modSrcFolder = args[2];

            foreach (var folder in new[] { oldSrcFolder, newSrcFolder, modSrcFolder })
            {
                if (!Directory.Exists(folder))
                {
                    Log($"Error: Directory '{folder}' does not exist.");
                    return;
                }
            }

            Log("Analyzing mod source code...");
            var patchMethods = ParseModSource(modSrcFolder);

            Log($"Found {patchMethods.Count} patch methods:");
            foreach (var pm in patchMethods)
            {
                Log($"  PatchFile: {pm.PatchFile}, PatchMethod: {pm.PatchMethod}, " +
                    $"TargetClass: {pm.TargetClass}, TargetMethod: {pm.TargetMethod}");
            }

            Log("Mapping game source files...");
            var oldClassMap = MapTypesToFiles(oldSrcFolder);
            var newClassMap = MapTypesToFiles(newSrcFolder);

            Log($"Mapped {oldClassMap.Count} types in old source ({oldSrcFolder}).");
            Log($"Mapped {newClassMap.Count} types in new source ({newSrcFolder}).");

            Log("Comparing patched methods...");
            var reportItems = AnalyzePatchedMethods(patchMethods, oldClassMap, newClassMap, modSrcFolder);

            if (reportItems.Count == 0)
            {
                if (patchMethods.Count == 0)
                {
                    Log("No patch methods were found in the mod source.");
                }
                else
                {
                    Log("No changes detected in any patched methods.");
                }
            }
            else
            {
                Log($"Found {reportItems.Count} changed methods. Generating HTML report...");
                GenerateHtmlReport(patchMethods.Count, reportItems);
                Log("Report generated: diff_report.html");
            }
        }

        static List<(string PatchFile, string PatchMethod, string TargetClass, string TargetMethod)> ParseModSource(string modSrcFolder)
        {
            var patchMethods = new List<(string, string, string, string)>();
            var csFiles = Directory.GetFiles(modSrcFolder, "*.cs", SearchOption.AllDirectories);

            foreach (var file in csFiles)
            {
                string code = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();
                var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var type in types)
                {
                    var classHarmonyPatchAttr = type.AttributeLists
                        .SelectMany(al => al.Attributes)
                        .FirstOrDefault(a => a.Name.ToString().Contains("HarmonyPatch"));

                    if (classHarmonyPatchAttr != null && classHarmonyPatchAttr.ArgumentList?.Arguments.Count >= 2)
                    {
                        var args = classHarmonyPatchAttr.ArgumentList.Arguments;
                        string targetClass = ExtractTargetClass(args[0]);
                        string targetMethod = ExtractTargetMethod(args[1]);

                        var methods = type.Members.OfType<MethodDeclarationSyntax>();
                        foreach (var method in methods)
                        {
                            if (method.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString().Contains("Harmony"))))
                            {
                                patchMethods.Add((file, method.Identifier.Text, targetClass, targetMethod));
                                Log($"Found type-level patch: {method.Identifier.Text} targeting {targetClass}.{targetMethod} in {file}");
                            }
                        }
                    }

                    var methodsInType = type.Members.OfType<MethodDeclarationSyntax>();
                    foreach (var method in methodsInType)
                    {
                        var attributes = method.AttributeLists.SelectMany(al => al.Attributes);
                        var harmonyPatchAttr = attributes.FirstOrDefault(a => a.Name.ToString().Contains("HarmonyPatch"));
                        var harmonyTypeAttr = attributes.FirstOrDefault(a => a.Name.ToString().Contains("Harmony") && a.Name.ToString() != "HarmonyPatch");

                        if (harmonyPatchAttr != null && harmonyTypeAttr != null && harmonyPatchAttr.ArgumentList?.Arguments.Count >= 2)
                        {
                            var args = harmonyPatchAttr.ArgumentList.Arguments;
                            string targetClass = ExtractTargetClass(args[0]);
                            string targetMethod = ExtractTargetMethod(args[1]);
                            patchMethods.Add((file, method.Identifier.Text, targetClass, targetMethod));
                            Log($"Found method-level patch: {method.Identifier.Text} targeting {targetClass}.{targetMethod} in {file}");
                        }
                    }
                }
            }

            return patchMethods;
        }

        static string ExtractTargetClass(AttributeArgumentSyntax arg)
        {
            if (arg.Expression is TypeOfExpressionSyntax typeOf)
            {
                return typeOf.Type.ToString();
            }

            return arg.ToString().Trim('"');
        }

        static string ExtractTargetMethod(AttributeArgumentSyntax arg)
        {
            if (arg.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return literal.Token.ValueText;
            }
            else if (arg.Expression is InvocationExpressionSyntax invocation && invocation.Expression.ToString() == "nameof")
            {
                if (invocation.ArgumentList.Arguments.Count == 1)
                {
                    var expr = invocation.ArgumentList.Arguments[0].Expression;
                    if (expr is MemberAccessExpressionSyntax memberAccess)
                    {
                        return memberAccess.Name.Identifier.Text;
                    }

                    return expr.ToString();
                }
            }

            return arg.ToString().Trim('"');
        }

        static Dictionary<string, string> MapTypesToFiles(string srcFolder)
        {
            var map = new Dictionary<string, string>();
            var csFiles = Directory.GetFiles(srcFolder, "*.cs", SearchOption.AllDirectories);

            foreach (var file in csFiles)
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
                var root = tree.GetRoot();
                var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var type in types)
                {
                    string fullName = GetFullTypeName(type);
                    map[fullName] = file;
                }
            }

            return map;
        }

        static string GetFullTypeName(TypeDeclarationSyntax type)
        {
            var namespaceNode = type.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
            string ns = namespaceNode?.Name.ToString() ?? "";
            return string.IsNullOrEmpty(ns) ? type.Identifier.Text : $"{ns}.{type.Identifier.Text}";
        }

        static List<(string Id, string Label, string DiffHtml, string OldSrc, string NewSrc, string PatchSrc)> AnalyzePatchedMethods(
            List<(string PatchFile, string PatchMethod, string TargetClass, string TargetMethod)> patchMethods,
            Dictionary<string, string> oldClassMap,
            Dictionary<string, string> newClassMap,
            string modSrcFolder)
        {
            var reportItems = new List<(string, string, string, string, string, string)>();
            int idCounter = 1;

            foreach (var (patchFile, patchMethod, targetClass, targetMethod) in patchMethods)
            {
                Log($"Processing patch: {patchFile}:{patchMethod} -> {targetClass}:{targetMethod}");

                string fullTargetClass = ResolveClassName(targetClass, oldClassMap, "old");
                if (fullTargetClass == null) continue;

                if (!newClassMap.ContainsKey(fullTargetClass))
                {
                    Log($"  Target class/struct '{fullTargetClass}' not found in new source.");
                    continue;
                }

                string oldFile = oldClassMap[fullTargetClass];
                string newFile = newClassMap[fullTargetClass];
                Log($"  Found target class/struct in old: {oldFile}");
                Log($"  Found target class/struct in new: {newFile}");

                var oldTree = CSharpSyntaxTree.ParseText(File.ReadAllText(oldFile));
                var newTree = CSharpSyntaxTree.ParseText(File.ReadAllText(newFile));

                var oldMethod = FindMethod(oldTree, targetMethod);
                var newMethod = FindMethod(newTree, targetMethod);

                if (oldMethod == null)
                {
                    Log($"  Target method '{targetMethod}' not found in old version.");
                    continue;
                }

                if (newMethod == null)
                {
                    Log($"  Target method '{targetMethod}' not found in new version.");
                    continue;
                }

                Log($"  Found target method in both versions.");

                var oldBodyTree = CSharpSyntaxTree.ParseText(oldMethod.Body?.ToString() ?? "{}").GetRoot();
                var newBodyTree = CSharpSyntaxTree.ParseText(newMethod.Body?.ToString() ?? "{}").GetRoot();

                if (!oldBodyTree.IsEquivalentTo(newBodyTree, topLevel: false))
                {
                    Log($"  Change detected in method '{targetMethod}'.");
                    string oldSrc = oldMethod.ToString();
                    string newSrc = newMethod.ToString();
                    string patchSrc = GetPatchMethodSource(patchFile, patchMethod);
                    string diffHtml = GenerateDiffHtml(oldSrc, newSrc);

                    string label = $"{Path.GetFileName(patchFile)}:{patchMethod} -> {Path.GetFileName(oldFile)}:{targetMethod}";
                    string id = $"method{idCounter++}";
                    reportItems.Add((id, label, diffHtml, HtmlEncode(oldSrc), HtmlEncode(newSrc), HtmlEncode(patchSrc)));
                }
                else
                {
                    Log($"  No changes detected in method '{targetMethod}'.");
                }
            }

            return reportItems;
        }

        static string ResolveClassName(string targetClass, Dictionary<string, string> classMap, string sourceName)
        {
            if (classMap.ContainsKey(targetClass))
            {
                return targetClass;
            }

            var candidates = classMap.Keys.Where(k => k.EndsWith("." + targetClass)).ToList();
            if (candidates.Count == 1)
            {
                Log($"  Resolved '{targetClass}' to '{candidates[0]}' in {sourceName} source.");
                return candidates[0];
            }
            else if (candidates.Count > 1)
            {
                Log($"  Multiple candidates for '{targetClass}' in {sourceName} source: {string.Join(", ", candidates)}");
            }
            else
            {
                Log($"  Target class/struct '{targetClass}' not found in {sourceName} source.");
            }

            return null;
        }

        static MethodDeclarationSyntax FindMethod(SyntaxTree tree, string methodName)
        {
            return tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);
        }

        static string GetPatchMethodSource(string filePath, string methodName)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath));
            var method = tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == methodName);
            return method?.ToString() ?? "// Patch method not found";
        }
        static string FormatLineForOldPane(DiffPiece line)
        {
            if (line.Type == ChangeType.Deleted)
            {
                return $"<span class=\"deleted\">{HtmlEncode(line.Text)}</span>";
            }
            else if (line.Type == ChangeType.Modified)
            {
                var sb = new StringBuilder();
                foreach (var piece in line.SubPieces)
                {
                    if (piece.Type == ChangeType.Unchanged || piece.Type == ChangeType.Deleted)
                    {
                        string text = HtmlEncode(piece.Text);
                        if (piece.Type == ChangeType.Deleted)
                        {
                            sb.Append($"<span class=\"deleted\">{text}</span>");
                        }
                        else
                        {
                            sb.Append(text);
                        }
                    }
                }
                return sb.ToString();
            }
            else if (line.Type == ChangeType.Unchanged)
            {
                return HtmlEncode(line.Text);
            }
            else // Inserted
            {
                return "";
            }
        }

        static string FormatLineForNewPane(DiffPiece line)
        {
            if (line.Type == ChangeType.Inserted)
            {
                return $"<span class=\"inserted\">{HtmlEncode(line.Text)}</span>";
            }
            else if (line.Type == ChangeType.Modified)
            {
                var sb = new StringBuilder();
                foreach (var piece in line.SubPieces)
                {
                    if (piece.Type == ChangeType.Unchanged || piece.Type == ChangeType.Inserted)
                    {
                        string text = HtmlEncode(piece.Text);
                        if (piece.Type == ChangeType.Inserted)
                        {
                            sb.Append($"<span class=\"inserted\">{text}</span>");
                        }
                        else
                        {
                            sb.Append(text);
                        }
                    }
                }
                return sb.ToString();
            }
            else if (line.Type == ChangeType.Unchanged)
            {
                return HtmlEncode(line.Text);
            }
            else // Deleted
            {
                return "";
            }
        }
        static string GenerateDiffHtml(string oldText, string newText)
        {
            var differ = new Differ();
            var builder = new InlineDiffBuilder(differ);
            var diff = builder.BuildDiffModel(oldText, newText);

            var sb = new StringBuilder();
            sb.AppendLine("<div class=\"diff-container\">");
            sb.AppendLine("<div class=\"diff-pane old-pane\">");
            int oldLineNum = 1;

            foreach (var line in diff.Lines)
            {
                string formattedContent = FormatLineForOldPane(line);
                string className = line.Type == ChangeType.Deleted ? "deleted-line" :
                    line.Type == ChangeType.Modified ? "modified-line" :
                    "unchanged";
                sb.AppendLine($"<pre class=\"line {className}\" data-line=\"{oldLineNum}\">{oldLineNum} {formattedContent}</pre>");
                oldLineNum++;
            }

            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"resizer diff-resizer\"></div>");
            sb.AppendLine("<div class=\"diff-pane new-pane\">");
            int newLineNum = 1;

            foreach (var line in diff.Lines)
            {
                string formattedContent = FormatLineForNewPane(line);
                string className = line.Type == ChangeType.Inserted ? "inserted-line" :
                    line.Type == ChangeType.Modified ? "modified-line" :
                    "unchanged";
                sb.AppendLine($"<pre class=\"line {className}\" data-line=\"{newLineNum}\">{newLineNum} {formattedContent}</pre>");
                newLineNum++;
            }

            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            return sb.ToString();
        }
        static string FormatDiffLine(string lineContent, IReadOnlyList<DiffPiece> subPieces)
        {
            if (subPieces == null || subPieces.Count == 0)
            {
                return HtmlEncode(lineContent) + "\n"; // Ensure newline is added
            }

            var result = new StringBuilder();
            int lastPos = 0;

            foreach (var piece in subPieces)
            {
                // Handle nullable Position with null check and explicit cast
                int position = piece.Position.HasValue ? piece.Position.Value : 0;

                // Ensure we don't go out of bounds
                if (position < 0 || position > lineContent.Length)
                {
                    position = 0; // Default to 0 if invalid
                }

                // Append unchanged text before the change
                if (position > lastPos)
                {
                    result.Append(HtmlEncode(lineContent.Substring(lastPos, position - lastPos)));
                }

                // Wrap changed text in a span with appropriate class based on piece type
                string className = piece.Type switch
                {
                    ChangeType.Deleted => "deleted",
                    ChangeType.Inserted => "inserted",
                    ChangeType.Modified => "modified",
                    _ => "unchanged"
                };
                result.Append($"<span class=\"{className}\">{HtmlEncode(piece.Text ?? "")}</span>");

                // Handle nullable Position for lastPos update with null check and explicit cast
                lastPos = position + (piece.Text?.Length ?? 0);
            }

            // Append any remaining unchanged text, ensuring we don't exceed lineContent length
            if (lastPos < lineContent.Length)
            {
                result.Append(HtmlEncode(lineContent.Substring(lastPos)));
            }

            // Ensure each line ends with a newline for proper formatting
            result.Append("\n");
            return result.ToString();
        }

        static string HtmlEncode(string? text)
        {
            return text == null ? "" : System.Net.WebUtility.HtmlEncode(text);
        }


        static void GenerateHtmlReport(int totalPatches, List<(string Id, string Label, string DiffHtml, string OldSrc, string NewSrc, string PatchSrc)> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"UTF-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine("<title>Diff1cult - Relevant Source Change Report</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(@"
                :root {
                    --primary: #2c3e50;
                    --secondary: #34495e;
                    --highlight: #3498db;
                    --text: #ecf0f1;
                    --deleted: #e74c3c;
                    --inserted: #2ecc71;
                    --modified: #f1c40f;
                    --line-num: #7f8c8d;
                }
                body {
                    font-family: 'Segoe UI', Arial, sans-serif;
                    margin: 0;
                    padding: 0;
                    background: var(--primary);
                    color: var(--text);
                    display: flex;
                    flex-direction: column;
                    height: 100vh;
                }
                #header {
                    background: var(--secondary);
                    padding: 15px 20px;
                    box-shadow: 0 2px 5px rgba(0,0,0,0.2);
                    z-index: 10;
                }
                #header h1 {
                    margin: 0;
                    font-size: 24px;
                }
                #summary {
                    margin-top: 10px;
                    font-style: italic;
                }
                #log-toggle {
                    background: var(--highlight);
                    border: none;
                    padding: 8px 15px;
                    color: white;
                    cursor: pointer;
                    border-radius: 5px;
                    margin-top: 10px;
                }
                #log-content {
                    display: none;
                    background: #ffffff;
                    color: #333;
                    padding: 20px;
                    box-sizing: border-box;
                    max-height: none;
                    overflow-y: auto;
                    margin-top: 10px;
                    border-radius: 5px;
                    box-shadow: 0 2px 5px rgba(0,0,0,0.1);
                }
                #content {
                    display: flex;
                    flex: 1;
                    overflow: hidden;
                }
                #left-panel {
                    width: 30%;
                    background: var(--secondary);
                    overflow-y: auto;
                    padding: 20px;
                    box-shadow: inset -2px 0 5px rgba(0,0,0,0.1);
                    resize: horizontal;
                    min-width: 200px;
                    max-width: 80%;
                }
                #resizer {
                    width: 5px;
                    background: #95a5a6;
                    cursor: col-resize;
                }
                #right-panel {
                    flex: 1;
                    padding: 20px;
                    overflow-y: auto;
                }
                #tabs {
                    margin-bottom: 15px;
                    display: flex;
                    gap: 10px;
                }
                .tab-button {
                    padding: 10px 20px;
                    background: var(--secondary);
                    border: none;
                    color: var(--text);
                    cursor: pointer;
                    border-radius: 5px;
                    transition: background 0.3s;
                }
                .tab-button:hover {
                    background: rgba(255,255,255,0.1);
                }
                .tab-button.active {
                    background: var(--highlight);
                }
                .tab-content {
                    display: none;
                    background: white;
                    color: #333;
                    padding: 15px;
                    border-radius: 5px;
                    box-shadow: 0 2px 5px rgba(0,0,0,0.1);
                    overflow-y: auto;
                    height: calc(100vh - 180px);
                }
                .tab-content.active {
                    display: block;
                }
                .diff-container {
                    display: flex;
                    gap: 0;
                    position: relative;
                }
                .diff-pane {
                    width: 50%;
                    background: #f9f9f9;
                    padding: 10px;
                    border-radius: 5px;
                    box-sizing: border-box;
                }
                .diff-resizer {
                    width: 5px;
                    background: #95a5a6;
                    cursor: col-resize;
                    position: absolute;
                    top: 0;
                    bottom: 0;
                    left: 50%;
                    transform: translateX(-50%);
                }
.line::before {
    content: attr(data-line);
    display: inline-block;
    width: 40px; /* Fixed width for line numbers */
    text-align: right;
    color: var(--line-num, #888);
    margin-right: 10px;
}
.line {
    display: block;
    white-space: pre;
    font-family: 'Consolas', monospace;
    font-size: 14px;
    padding-left: 50px;
    line-height: 1.5em; /* Uniform line spacing */
    min-height: 1.5em; /* Ensures empty lines match code lines */
}
                .deleted, .inserted, .modified {
                    display: inline;
                }
                .deleted { color: var(--deleted); text-decoration: line-through; }
                .inserted { color: var(--inserted); }
                .modified { color: var(--modified); }
                .unchanged { color: #333; }
.deleted-line {
    background-color: #ffdddd; /* Light red background for deleted lines */
}
.inserted-line {
    background-color: #ddffdd; /* Light green background for inserted lines */
}
.modified-line {
    background-color: #ffffdd; /* Light yellow background for modified lines */
}
                #left-panel li.active {
                    background: var(--highlight);
                    color: white;
                    font-weight: bold;
                    border: 2px solid white;
                    box-shadow: 0 0 5px rgba(0,0,0,0.3);
                }

            ");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<div id=\"header\">");
            sb.AppendLine("<h1>Diff1cult - Relevant Source Change Report</h1>");
            sb.AppendLine($"<div id=\"summary\">Found {totalPatches} Patched Methods. Changes Detected in {items.Count}.</div>");
            sb.AppendLine("<button id=\"log-toggle\">Show Log</button>");
            sb.AppendLine($"<div id=\"log-content\"><pre>{HtmlEncode(consoleLog.ToString())}</pre></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<div id=\"content\">");
            sb.AppendLine("<div id=\"left-panel\">");
            sb.AppendLine("<ul>");
            foreach (var item in items)
            {
                sb.AppendLine($"<li data-id=\"{item.Id}\">{item.Label}</li>");
            }

            sb.AppendLine("</ul>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div id=\"resizer\"></div>");
            sb.AppendLine("<div id=\"right-panel\">");
            sb.AppendLine("<div id=\"tabs\">");
            sb.AppendLine("<button class=\"tab-button active\" data-tab=\"diff\">Diff</button>");
            sb.AppendLine("<button class=\"tab-button\" data-tab=\"old\">Old Src</button>");
            sb.AppendLine("<button class=\"tab-button\" data-tab=\"new\">New Src</button>");
            sb.AppendLine("<button class=\"tab-button\" data-tab=\"patch\">Patch Src</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div id=\"diff-content\" class=\"tab-content active\"></div>");
            sb.AppendLine("<div id=\"old-content\" class=\"tab-content\"></div>");
            sb.AppendLine("<div id=\"new-content\" class=\"tab-content\"></div>");
            sb.AppendLine("<div id=\"patch-content\" class=\"tab-content\"></div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<script>");
            sb.AppendLine("var methods = {");
            foreach (var item in items)
            {
                sb.AppendLine($"  '{item.Id}': {{");
                sb.AppendLine($"    diff: `{item.DiffHtml}`,");
                sb.AppendLine($"    old: `<pre><code>{item.OldSrc}</code></pre>`,");
                sb.AppendLine($"    new: `<pre><code>{item.NewSrc}</code></pre>`,");
                sb.AppendLine($"    patch: `<pre><code>{item.PatchSrc}</code></pre>`");
                sb.AppendLine("  },");
            }

            sb.AppendLine("};");

            sb.AppendLine(@"
                function selectMethod(id) {
                    var method = methods[id];
                    if (!method) return;
                    document.getElementById('diff-content').innerHTML = method.diff;
                    document.getElementById('old-content').innerHTML = method.old;
                    document.getElementById('new-content').innerHTML = method.new;
                    document.getElementById('patch-content').innerHTML = method.patch;
                    var lis = document.querySelectorAll('#left-panel li');
                    lis.forEach(li => li.classList.remove('active'));
                    document.querySelector(`#left-panel li[data-id='${id}']`).classList.add('active');
                    var activeTab = document.querySelector('.tab-button.active').dataset.tab;
                    showTab(activeTab);
                    setupDiffResizer();
                }

                function showTab(tabId) {
                    var contents = document.querySelectorAll('.tab-content');
                    contents.forEach(content => content.classList.remove('active'));
                    document.getElementById(`${tabId}-content`).classList.add('active');
                    var buttons = document.querySelectorAll('.tab-button');
                    buttons.forEach(btn => btn.classList.remove('active'));
                    document.querySelector(`.tab-button[data-tab='${tabId}']`).classList.add('active');
                    if (tabId === 'diff') setupDiffResizer();
                }

                function setupPaneResizer() {
                    const leftPanel = document.getElementById('left-panel');
                    const resizer = document.getElementById('resizer');
                    const rightPanel = document.getElementById('right-panel');
                    let isResizing = false;

                    resizer.addEventListener('mousedown', (e) => {
                        isResizing = true;
                        document.body.style.cursor = 'col-resize';
                    });

                    document.addEventListener('mousemove', (e) => {
                        if (!isResizing) return;
                        const containerWidth = document.getElementById('content').offsetWidth;
                        const newLeftWidth = (e.clientX / containerWidth) * 100;
                        if (newLeftWidth >= 20 && newLeftWidth <= 80) {
                            leftPanel.style.width = `${newLeftWidth}%`;
                        }
                    });

                    document.addEventListener('mouseup', () => {
                        isResizing = false;
                        document.body.style.cursor = 'default';
                    });
                }

                function setupDiffResizer() {
                    const diffContainer = document.querySelector('.diff-container');
                    if (!diffContainer) return;
                    const oldPane = diffContainer.querySelector('.old-pane');
                    const resizer = diffContainer.querySelector('.diff-resizer');
                    const newPane = diffContainer.querySelector('.new-pane');
                    let isResizing = false;

                    resizer.addEventListener('mousedown', (e) => {
                        isResizing = true;
                        document.body.style.cursor = 'col-resize';
                    });

                    document.addEventListener('mousemove', (e) => {
                        if (!isResizing) return;
                        const containerWidth = diffContainer.offsetWidth;
                        const newOldWidth = (e.clientX - diffContainer.offsetLeft) / containerWidth * 100;
                        if (newOldWidth >= 20 && newOldWidth <= 80) {
                            oldPane.style.width = `${newOldWidth}%`;
                            newPane.style.width = `${100 - newOldWidth}%`;
                            resizer.style.left = `${newOldWidth}%`;
                        }
                    });

                    document.addEventListener('mouseup', () => {
                        isResizing = false;
                        document.body.style.cursor = 'default';
                    });
                }

                document.querySelectorAll('#left-panel li').forEach(li => {
                    li.addEventListener('click', () => selectMethod(li.dataset.id));
                });
                document.querySelectorAll('.tab-button').forEach(btn => {
                    btn.addEventListener('click', () => showTab(btn.dataset.tab));
                });

                var toggleBtn = document.getElementById('log-toggle');
                var logContent = document.getElementById('log-content');
                toggleBtn.addEventListener('click', () => {
                    if (logContent.style.display === 'block') {
                        logContent.style.display = 'none';
                        toggleBtn.textContent = 'Show Log';
                    } else {
                        logContent.style.display = 'block';
                        toggleBtn.textContent = 'Hide Log';
                    }
                });

                var firstLi = document.querySelector('#left-panel li');
                if (firstLi) selectMethod(firstLi.dataset.id);
                setupPaneResizer();
            ");
            sb.AppendLine("</script>");

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText("diff_report.html", sb.ToString());
        }
    }
}