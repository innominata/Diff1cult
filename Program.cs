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
using DiffPlex.Chunkers;
using System.Text.RegularExpressions;

namespace UnityDiffTool
{
    class Program
    {
        static StringBuilder consoleLog = new StringBuilder();

        static void Log(string message)
        {
            // Log to both the console log buffer and directly to console for immediate visibility
            consoleLog.AppendLine(message);
            Console.WriteLine(message); // Direct console output
        }

        static void Main(string[] args)
        {
            try
            {
                // Add debug test case to verify our diffing
                TestInlineDiff();
                
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

                // Add a test diff to demonstrate different highlighting styles
                reportItems.Insert(0, GenerateTestDiff());

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
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Environment.Exit(1);
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

                string? fullTargetClass = ResolveClassName(targetClass, oldClassMap, "old");
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

        static string? ResolveClassName(string targetClass, Dictionary<string, string> classMap, string sourceName)
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

        static MethodDeclarationSyntax? FindMethod(SyntaxTree tree, string methodName)
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

        static string GenerateDiffHtml(string oldText, string newText)
        {
            Console.WriteLine("DEBUG: GenerateDiffHtml called");
            
            // Create a line-level differ
            var differ = new Differ();
            var builder = new InlineDiffBuilder(differ);
            var diff = builder.BuildDiffModel(
                oldText,
                newText,
                ignoreWhitespace: false,
                ignoreCase: false,
                new LineEndingsPreservingChunker()
            );

            Console.WriteLine($"DEBUG: Found {diff.Lines.Count} total diff lines");
            Console.WriteLine($"DEBUG: Deleted lines: {diff.Lines.Count(l => l.Type == ChangeType.Deleted)}");
            Console.WriteLine($"DEBUG: Inserted lines: {diff.Lines.Count(l => l.Type == ChangeType.Inserted)}");
            
            // Split lines for our own processing
            var oldLines = oldText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var newLines = newText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            
            // First, identify modified lines (similar lines that were changed)
            var deletedLines = diff.Lines.Where(l => l.Type == ChangeType.Deleted).ToList();
            var insertedLines = diff.Lines.Where(l => l.Type == ChangeType.Inserted).ToList();
            
            // For each deleted line, try to find a similar inserted line
            var modifiedPairs = new List<(DiffPiece Deleted, DiffPiece Inserted)>();
            var remainingInserted = new List<DiffPiece>(insertedLines);
            var remainingDeleted = new List<DiffPiece>(deletedLines);
            
            // Match lines that are more than 50% similar
            foreach (var deleted in deletedLines)
            {
                var bestMatch = remainingInserted
                    .Select(inserted => new { 
                        Line = inserted, 
                        Similarity = CalculateSimilarity(deleted.Text, inserted.Text) 
                    })
                    .Where(match => match.Similarity > 0.5)
                    .OrderByDescending(match => match.Similarity)
                    .FirstOrDefault();
                
                if (bestMatch != null)
                {
                    // Found a similar line - treat as modified
                    Console.WriteLine($"DEBUG: Found modified line pair:");
                    Console.WriteLine($"  Old: '{deleted.Text}'");
                    Console.WriteLine($"  New: '{bestMatch.Line.Text}'");
                    Console.WriteLine($"  Similarity: {bestMatch.Similarity:P1}");
                    
                    modifiedPairs.Add((deleted, bestMatch.Line));
                    remainingInserted.Remove(bestMatch.Line);
                    remainingDeleted.Remove(deleted);
                }
            }
            
            // Build the HTML
            var sb = new StringBuilder();
            sb.AppendLine("<div class=\"diff-container\">");
            
            // Old pane
            sb.AppendLine("<div class=\"diff-pane old-pane\">");
            int oldLineNum = 1;
            int oldIndex = 0;
            
            foreach (var line in diff.Lines)
            {
                if (line.Type == ChangeType.Inserted)
                {
                    // For inserted lines in new pane, add a blank line in old pane
                    var pair = modifiedPairs.FirstOrDefault(p => p.Inserted == line);
                    if (pair.Deleted == null)
                    {
                        // This is a pure insertion, add blank line
                        sb.AppendLine($"<pre class=\"line unchanged-line empty-line\" data-line=\"{oldLineNum++}\"></pre>");
                    }
                    continue;
                }
                
                var cssClass = line.Type == ChangeType.Deleted ? "deleted-line" : "unchanged-line";
                
                // Check if this is part of a modified pair
                var pair2 = modifiedPairs.FirstOrDefault(p => p.Deleted == line);
                if (pair2.Inserted != null)
                {
                    // This is a modified line - do character level diff
                    string html = ExplicitSimpleDiff(line.Text, pair2.Inserted.Text, true);
                    sb.AppendLine($"<pre class=\"line modified-line\" data-line=\"{oldLineNum++}\">{html}</pre>");
                }
                else
                {
                    // Regular deleted or unchanged line
                    sb.AppendLine($"<pre class=\"line {cssClass}\" data-line=\"{oldLineNum++}\">{HtmlEncode(line.Text)}</pre>");
                }
                
                oldIndex++;
            }
            
            sb.AppendLine("</div>");
            
            // New pane
            sb.AppendLine("<div class=\"diff-pane new-pane\">");
            int newLineNum = 1;
            int newIndex = 0;
            
            foreach (var line in diff.Lines)
            {
                if (line.Type == ChangeType.Deleted)
                {
                    // For deleted lines in old pane, add a blank line in new pane
                    var pair3 = modifiedPairs.FirstOrDefault(p => p.Deleted == line);
                    if (pair3.Inserted == null)
                    {
                        // This is a pure deletion, add blank line
                        sb.AppendLine($"<pre class=\"line unchanged-line empty-line\" data-line=\"{newLineNum++}\"></pre>");
                    }
                    continue;
                }
                
                var cssClass = line.Type == ChangeType.Inserted ? "inserted-line" : "unchanged-line";
                
                // Check if this is part of a modified pair
                var pair4 = modifiedPairs.FirstOrDefault(p => p.Inserted == line);
                if (pair4.Deleted != null)
                {
                    // This is a modified line - do character level diff
                    string html = ExplicitSimpleDiff(pair4.Deleted.Text, line.Text, false);
                    sb.AppendLine($"<pre class=\"line modified-line\" data-line=\"{newLineNum++}\">{html}</pre>");
                }
                else
                {
                    // Regular inserted or unchanged line
                    sb.AppendLine($"<pre class=\"line {cssClass}\" data-line=\"{newLineNum++}\">{HtmlEncode(line.Text)}</pre>");
                }
                
                newIndex++;
            }
            
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            return sb.ToString();
        }
        
        // Calculate similarity between two strings (simple Levenshtein distance based)
        static double CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0;
            
            int maxLength = Math.Max(s1.Length, s2.Length);
            if (maxLength == 0) return 1.0;
            
            return (1.0 - ((double)LevenshteinDistance(s1, s2) / maxLength));
        }
        
        // Compute Levenshtein distance
        static int LevenshteinDistance(string s1, string s2)
        {
            int[,] d = new int[s1.Length + 1, s2.Length + 1];
            
            for (int i = 0; i <= s1.Length; i++)
                d[i, 0] = i;
            
            for (int j = 0; j <= s2.Length; j++)
                d[0, j] = j;
            
            for (int j = 1; j <= s2.Length; j++)
            {
                for (int i = 1; i <= s1.Length; i++)
                {
                    int cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;
                    
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            
            return d[s1.Length, s2.Length];
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

        // New method to generate a test diff
        static (string Id, string Label, string DiffHtml, string OldSrc, string NewSrc, string PatchSrc) GenerateTestDiff()
        {
            string oldCode = @"public void TestMethod() 
{
    // This line will have inline changes
    int value = 42;
    string name = ""oldName"";
    
    // This line will be completely removed
    Console.WriteLine(""This line is going to be deleted"");
    
    // This line will stay the same
    bool isEnabled = true;
    
    // Common code with some changes
    if (isEnabled) 
    {
        DoSomething(""old parameter"");
    }
}";

            string newCode = @"public void TestMethod() 
{
    // This line will have inline changes
    int value = 100;
    string name = ""newName"";
    
    // This line will be completely added
    Console.WriteLine(""This is a brand new line"");
    
    // This line will stay the same
    bool isEnabled = true;
    
    // Common code with some changes
    if (isEnabled) 
    {
        DoSomething(""new parameter"");
    }
}";

            string patchCode = @"[HarmonyPatch]
public void TestPatchMethod()
{
    // This is just a sample patch method
    if (!__state)
        return;
        
    // Do patch things here
    __result = true;
}";

            string diffHtml = GenerateDiffHtml(oldCode, newCode);
            
            return ("test-diff", "Test Diff Example", diffHtml, HtmlEncode(oldCode), HtmlEncode(newCode), HtmlEncode(patchCode));
        }

        // Test method to verify inline diffing
        static void TestInlineDiff()
        {
            Console.WriteLine("======= TESTING INLINE DIFF =======");
            
            // Test case 1: Simple value change
            string oldLine = "    int value = 42;";
            string newLine = "    int value = 100;";
            
            // Direct test
            Console.WriteLine("\nTest case 1: Simple value change");
            string result1 = ExplicitSimpleDiff(oldLine, newLine, true);
            Console.WriteLine($"HTML Result: {result1}");
            
            // Test case 2: Multiple value changes
            string oldLine2 = "    int value = 42, count = 10;";
            string newLine2 = "    int value = 100, count = 20;";
            
            Console.WriteLine("\nTest case 2: Multiple value changes");
            string result2 = ExplicitSimpleDiff(oldLine2, newLine2, true);
            Console.WriteLine($"HTML Result: {result2}");
            
            // Test the HTML generation
            Console.WriteLine("\nTesting full diff HTML generation");
            string html = GenerateDiffHtml(
                "public void TestMethod()\n{\n    int value = 42;\n    Console.WriteLine(value);\n}", 
                "public void TestMethod()\n{\n    int value = 100;\n    Console.WriteLine(value);\n}"
            );
            
            Console.WriteLine($"Full HTML Result:\n{html}");
            Console.WriteLine("======= END TESTING INLINE DIFF =======");
        }

        // Very direct, simple diffing that focuses on literal values
        static string ExplicitSimpleDiff(string oldLine, string newLine, bool isOldVersion)
        {
            // Debug directly to console in case Log method isn't working
            Console.WriteLine($"DEBUG ExplicitSimpleDiff: oldLine='{oldLine}', newLine='{newLine}', isOldVersion={isOldVersion}");
            
            // Log inputs for debugging
            Log($"ExplicitSimpleDiff called with:");
            Log($"  oldLine: '{oldLine}'");
            Log($"  newLine: '{newLine}'");
            Log($"  isOldVersion: {isOldVersion}");
            
            // Split by common code delimiters
            string[] delimiters = new[] { " ", "\t", "(", ")", "[", "]", "{", "}", ";", ",", ".", ":", "=", "+", "-", "*", "/", "<", ">", "&&", "||" };
            
            // Manual pattern for finding literals like "42", "100", etc.
            var numberPattern = @"\b\d+\b";
            var stringPattern = @"""[^""]*""";
            var literalPattern = $"({numberPattern}|{stringPattern})";
            
            var oldMatches = Regex.Matches(oldLine, literalPattern);
            var newMatches = Regex.Matches(newLine, literalPattern);
            
            // Log regex matches
            Log($"  Found {oldMatches.Count} literals in oldLine:");
            for (int i = 0; i < oldMatches.Count; i++)
            {
                Log($"    [{i}] '{oldMatches[i].Value}' at position {oldMatches[i].Index}");
            }
            
            Log($"  Found {newMatches.Count} literals in newLine:");
            for (int i = 0; i < newMatches.Count; i++)
            {
                Log($"    [{i}] '{newMatches[i].Value}' at position {newMatches[i].Index}");
            }
            
            // If we can't find literals, just return encoded text
            if ((oldMatches.Count == 0 && newMatches.Count == 0) || oldLine == newLine)
            {
                Log("  No literals found or lines are identical - returning full line");
                return HtmlEncode(isOldVersion ? oldLine : newLine);
            }
            
            // Look for cases where the only difference is a single number/string literal
            if (oldMatches.Count == newMatches.Count)
            {
                List<int> diffIndices = new List<int>();
                
                for (int i = 0; i < oldMatches.Count; i++)
                {
                    if (oldMatches[i].Value != newMatches[i].Value)
                    {
                        diffIndices.Add(i);
                        Log($"    Difference found at index {i}: '{oldMatches[i].Value}' vs '{newMatches[i].Value}'");
                    }
                }
                
                // Only one literal different - ideal case for inline diff
                if (diffIndices.Count == 1)
                {
                    int idx = diffIndices[0];
                    string line = isOldVersion ? oldLine : newLine;
                    var match = isOldVersion ? oldMatches[idx] : newMatches[idx];
                    
                    string before = line.Substring(0, match.Index);
                    string value = match.Value;
                    string after = line.Substring(match.Index + match.Length);
                    
                    string cssClass = isOldVersion ? "deleted" : "inserted";
                    Log($"  Single literal difference found - returning with highlighted '{value}'");
                    return $"{HtmlEncode(before)}<span class=\"{cssClass}\">{HtmlEncode(value)}</span>{HtmlEncode(after)}";
                }
                else 
                {
                    Log($"  Found {diffIndices.Count} differences - not a single literal change");
                }
            }
            
            // Special case for "value = 42" -> "value = 100"
            var valuePatternOld = Regex.Match(oldLine, @"(\w+\s*=\s*)(\d+|\""[^""]*\"")(.*)");
            var valuePatternNew = Regex.Match(newLine, @"(\w+\s*=\s*)(\d+|\""[^""]*\"")(.*)");
            
            Log("  Checking for assignment pattern");
            Log($"  Old match success: {valuePatternOld.Success}, Groups: {valuePatternOld.Groups.Count}");
            if (valuePatternOld.Success)
            {
                for (int i = 0; i < valuePatternOld.Groups.Count; i++)
                {
                    Log($"    Group[{i}]: '{valuePatternOld.Groups[i].Value}'");
                }
            }
            
            Log($"  New match success: {valuePatternNew.Success}, Groups: {valuePatternNew.Groups.Count}");
            if (valuePatternNew.Success)
            {
                for (int i = 0; i < valuePatternNew.Groups.Count; i++)
                {
                    Log($"    Group[{i}]: '{valuePatternNew.Groups[i].Value}'");
                }
            }
            
            if (valuePatternOld.Success && valuePatternNew.Success && 
                valuePatternOld.Groups[1].Value == valuePatternNew.Groups[1].Value &&
                valuePatternOld.Groups[3].Value == valuePatternNew.Groups[3].Value)
            {
                // This is the exact case we're looking for - assignment with different values
                Log("  Found exact assignment pattern with different values!");
                if (isOldVersion)
                {
                    return $"{HtmlEncode(valuePatternOld.Groups[1].Value)}<span class=\"deleted\">{HtmlEncode(valuePatternOld.Groups[2].Value)}</span>{HtmlEncode(valuePatternOld.Groups[3].Value)}";
                }
                else
                {
                    return $"{HtmlEncode(valuePatternNew.Groups[1].Value)}<span class=\"inserted\">{HtmlEncode(valuePatternNew.Groups[2].Value)}</span>{HtmlEncode(valuePatternNew.Groups[3].Value)}";
                }
            }
            
            // Fallback - return the whole line
            Log("  No special case found - returning whole line");
            return HtmlEncode(isOldVersion ? oldLine : newLine);
        }
    }
}