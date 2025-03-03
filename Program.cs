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
        static bool verboseMode = false; // Flag to control debug logging

        static void Log(string message)
        {
            // Log to both the console log buffer and directly to console for immediate visibility
            consoleLog.AppendLine(message);
            
            // Only print DEBUG messages in verbose mode, but always print other messages
            if (verboseMode || !message.StartsWith("DEBUG"))
            {
                Console.WriteLine(message);
            }
        }

        static void Main(string[] args)
        {
            try
            {
                // Check for flags
                verboseMode = args.Contains("--verbose");
                bool testMode = args.Contains("--test");
                
                // Get non-flag arguments
                var nonFlagArgs = args.Where(arg => !arg.StartsWith("--")).ToArray();
                
                if (nonFlagArgs.Length < 3)
                {
                    Console.WriteLine("Usage: UnityDiffTool.exe <mod source folder> <old src folder> <new src folder> [--verbose] [--test]");
                    Console.WriteLine("  --verbose: Enable detailed debug output");
                    Console.WriteLine("  --test: Include test diff example in output");
                    return;
                }

                string modSrcFolder = nonFlagArgs[0];
                string oldSrcFolder = nonFlagArgs[1];
                string newSrcFolder = nonFlagArgs[2];

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

                // Add test diff if test mode is enabled
                if (testMode)
                {
                    reportItems.Insert(0, GenerateTestDiff());
                }

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
            Log("DEBUG: GenerateDiffHtml called");
            
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

            if (verboseMode)
            {
                Console.WriteLine($"DEBUG: Found {diff.Lines.Count} total diff lines");
                Console.WriteLine($"DEBUG: Deleted lines: {diff.Lines.Count(l => l.Type == ChangeType.Deleted)}");
                Console.WriteLine($"DEBUG: Inserted lines: {diff.Lines.Count(l => l.Type == ChangeType.Inserted)}");
            }
            
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
                    if (verboseMode)
                    {
                        Console.WriteLine($"DEBUG: Found modified line pair:");
                        Console.WriteLine($"  Old: '{deleted.Text}'");
                        Console.WriteLine($"  New: '{bestMatch.Line.Text}'");
                        Console.WriteLine($"  Similarity: {bestMatch.Similarity:P1}");
                    }
                    
                    modifiedPairs.Add((deleted, bestMatch.Line));
                    remainingInserted.Remove(bestMatch.Line);
                    remainingDeleted.Remove(deleted);
                }
            }
            
            // Build mapping of lines to their numbers in the original files
            Dictionary<DiffPiece, int> oldLineNumbers = new Dictionary<DiffPiece, int>();
            Dictionary<DiffPiece, int> newLineNumbers = new Dictionary<DiffPiece, int>();
            
            int oldLineCounter = 1;
            int newLineCounter = 1;
            
            foreach (var line in diff.Lines)
            {
                if (line.Type != ChangeType.Inserted)
                {
                    oldLineNumbers[line] = oldLineCounter++;
                }
                
                if (line.Type != ChangeType.Deleted)
                {
                    newLineNumbers[line] = newLineCounter++;
                }
            }
            
            // Build the HTML
            var sb = new StringBuilder();
            sb.AppendLine("<div class=\"diff-container\">");
            
            // Add the resizer between panes
            sb.AppendLine("<div class=\"diff-resizer\"></div>");
            
            // Left pane (old version)
            sb.AppendLine("<div class=\"old-pane\">");
            
            // Process all lines for the old pane
            foreach (var line in diff.Lines)
            {
                // Skip inserted lines in the old pane
                if (line.Type == ChangeType.Inserted)
                {
                    // For inserted lines that are part of a modified pair, we've already shown them
                    var pair = modifiedPairs.FirstOrDefault(p => p.Inserted == line);
                    if (pair.Deleted == null)
                    {
                        // This is a pure insertion, add an empty line
                        sb.AppendLine("<div class=\"diff-line empty-line\">");
                        sb.AppendLine($"  <div class=\"line-numbers\"><span class=\"old-line-num\">...</span><span class=\"new-line-num\">{newLineNumbers[line]}</span></div>");
                        sb.AppendLine("  <div class=\"line-content\"></div>");
                        sb.AppendLine("</div>");
                    }
                    continue;
                }
                
                // Determine line type and CSS class
                string cssClass;
                string oldLineNum = oldLineNumbers[line].ToString();
                string newLineNum = "...";
                string content;
                
                if (line.Type == ChangeType.Deleted)
                {
                    cssClass = "deleted-line";
                    
                    // Check if this is part of a modified pair
                    var pair = modifiedPairs.FirstOrDefault(p => p.Deleted == line);
                    if (pair.Inserted != null)
                    {
                        cssClass = "modified-line deleted-part";
                        newLineNum = newLineNumbers[pair.Inserted].ToString();
                        
                        // Use inline diff for modified lines
                        content = ExplicitSimpleDiff(line.Text, pair.Inserted.Text, true);
                    }
                    else
                    {
                        // Pure deletion
                        content = HtmlEncode(line.Text);
                    }
                }
                else // Unchanged
                {
                    cssClass = "unchanged-line";
                    newLineNum = newLineNumbers[line].ToString();
                    content = HtmlEncode(line.Text);
                }
                
                // Create the line with dual line numbers
                sb.AppendLine($"<div class=\"diff-line {cssClass}\">");
                sb.AppendLine($"  <div class=\"line-numbers\"><span class=\"old-line-num\">{oldLineNum}</span><span class=\"new-line-num\">{newLineNum}</span></div>");
                sb.AppendLine($"  <div class=\"line-content\">{content}</div>");
                sb.AppendLine("</div>");
            }
            
            sb.AppendLine("</div>"); // End old-pane
            
            // Right pane (new version)
            sb.AppendLine("<div class=\"new-pane\">");
            
            // Process all lines for the new pane
            foreach (var line in diff.Lines)
            {
                // Skip deleted lines in the new pane
                if (line.Type == ChangeType.Deleted)
                {
                    // For deleted lines that are part of a modified pair, we've already shown them
                    var pair = modifiedPairs.FirstOrDefault(p => p.Deleted == line);
                    if (pair.Inserted == null)
                    {
                        // This is a pure deletion, add an empty line
                        sb.AppendLine("<div class=\"diff-line empty-line\">");
                        sb.AppendLine($"  <div class=\"line-numbers\"><span class=\"old-line-num\">{oldLineNumbers[line]}</span><span class=\"new-line-num\">...</span></div>");
                        sb.AppendLine("  <div class=\"line-content\"></div>");
                        sb.AppendLine("</div>");
                    }
                    continue;
                }
                
                // Determine line type and CSS class
                string cssClass;
                string oldLineNum = "...";
                string newLineNum = newLineNumbers[line].ToString();
                string content;
                
                if (line.Type == ChangeType.Inserted)
                {
                    cssClass = "inserted-line";
                    
                    // Check if this is part of a modified pair
                    var pair = modifiedPairs.FirstOrDefault(p => p.Inserted == line);
                    if (pair.Deleted != null)
                    {
                        cssClass = "modified-line inserted-part";
                        oldLineNum = oldLineNumbers[pair.Deleted].ToString();
                        
                        // Use inline diff for modified lines
                        content = ExplicitSimpleDiff(pair.Deleted.Text, line.Text, false);
                    }
                    else
                    {
                        // Pure insertion
                        content = HtmlEncode(line.Text);
                    }
                }
                else // Unchanged
                {
                    cssClass = "unchanged-line";
                    oldLineNum = oldLineNumbers[line].ToString();
                    content = HtmlEncode(line.Text);
                }
                
                // Create the line with dual line numbers
                sb.AppendLine($"<div class=\"diff-line {cssClass}\">");
                sb.AppendLine($"  <div class=\"line-numbers\"><span class=\"old-line-num\">{oldLineNum}</span><span class=\"new-line-num\">{newLineNum}</span></div>");
                sb.AppendLine($"  <div class=\"line-content\">{content}</div>");
                sb.AppendLine("</div>");
            }
            
            sb.AppendLine("</div>"); // End new-pane
            sb.AppendLine("</div>"); // End diff-container
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
                    --gutter-bg: #1e272e;
                    --line-hover: rgba(52, 152, 219, 0.1);
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
                h1, h2, h3, h4 {
                    margin: 0;
                    font-weight: 600;
                }
                h1 {
                    font-size: 1.5rem;
                    color: var(--text);
                }
                h2 {
                    font-size: 1.3rem;
                    color: var(--text);
                    margin-bottom: 10px;
                }
                #content {
                    flex: 1;
                    display: flex;
                    overflow: hidden;
                }
                #left-panel {
                    width: 25%;
                    min-width: 200px;
                    background: var(--secondary);
                    overflow-y: auto;
                    padding: 15px 0;
                    border-right: 1px solid rgba(0,0,0,0.1);
                }
                #left-panel ul {
                    list-style: none;
                    padding: 0;
                    margin: 0;
                }
                #left-panel li {
                    padding: 10px 15px;
                    cursor: pointer;
                    border-left: 3px solid transparent;
                }
                #left-panel li:hover {
                    background: rgba(0,0,0,0.1);
                }
                #left-panel li.active {
                    background: rgba(0,0,0,0.15);
                    border-left-color: var(--highlight);
                }
                #resizer {
                    width: 6px;
                    background: var(--secondary);
                    cursor: col-resize;
                    position: relative;
                    z-index: 10;
                }
                #right-panel {
                    flex: 1;
                    display: flex;
                    flex-direction: column;
                    overflow: hidden;
                }
                #tabs {
                    display: flex;
                    background: var(--secondary);
                    border-bottom: 1px solid rgba(0,0,0,0.1);
                }
                .tab-button {
                    padding: 12px 20px;
                    cursor: pointer;
                    background: none;
                    border: none;
                    color: var(--text);
                    font-weight: 500;
                    border-bottom: 2px solid transparent;
                    opacity: 0.7;
                    transition: all 0.2s ease;
                }
                .tab-button:hover {
                    opacity: 0.9;
                }
                .tab-button.active {
                    opacity: 1;
                    border-bottom-color: var(--highlight);
                }
                .tab-content {
                    display: none;
                    flex: 1;
                    overflow: auto;
                    padding: 20px;
                }
                .tab-content.active {
                    display: block;
                }
                pre {
                    margin: 0;
                    font-family: 'Cascadia Code', 'Consolas', monospace;
                    font-size: 14px;
                    line-height: 1.4;
                    white-space: pre;
                    tab-size: 4;
                    padding: 5px 0;
                }
                code {
                    font-family: 'Cascadia Code', 'Consolas', monospace;
                    font-size: 14px;
                    white-space: pre;
                    tab-size: 4;
                }
                .source-code {
                    background: #272a3a;
                    padding: 10px;
                    overflow-x: auto;
                    border-radius: 0 0 5px 5px;
                }
                .diff-container {
                    font-family: 'Cascadia Code', 'Consolas', monospace;
                    font-size: 14px;
                    line-height: 1.4;
                    overflow: hidden;
                    background: #272a3a;
                    border-radius: 4px;
                    display: flex;
                    height: calc(100vh - 150px);
                    position: relative;
                }
                /* Two-pane diff view style */
                .old-pane, .new-pane {
                    overflow: auto;
                    background: #272a3a;
                    width: 50%;
                    min-width: 20%;
                    max-width: 80%;
                }
                .diff-resizer {
                    width: 6px;
                    background: rgba(127, 140, 141, 0.3);
                    cursor: col-resize;
                    position: absolute;
                    left: 50%;
                    top: 0;
                    bottom: 0;
                    z-index: 100;
                    transform: translateX(-50%);
                    transition: background-color 0.2s;
                }
                .diff-resizer:hover, .diff-resizer.resizing {
                    background: rgba(127, 140, 141, 0.8);
                }
                .diff-line {
                    display: flex;
                    white-space: pre;
                }
                .diff-line:hover {
                    background-color: var(--line-hover);
                }
                .line-numbers {
                    user-select: none;
                    text-align: right;
                    color: var(--line-num);
                    padding: 0 10px;
                    min-width: 100px;
                    background-color: var(--gutter-bg);
                    border-right: 1px solid rgba(127, 140, 141, 0.2);
                    font-variant-numeric: tabular-nums;
                    white-space: pre;
                }
                .old-line-num, .new-line-num {
                    display: inline-block;
                    width: 40px;
                    text-align: right;
                    padding-right: 5px;
                }
                .line-content {
                    padding-left: 15px;
                    white-space: pre;
                    width: 100%;
                    overflow-x: visible;
                }
                .deleted-line {
                    background-color: rgba(231, 76, 60, 0.12);
                }
                .deleted-line .line-content {
                    background-color: rgba(231, 76, 60, 0.12);
                }
                .inserted-line {
                    background-color: rgba(46, 204, 113, 0.12);
                }
                .inserted-line .line-content {
                    background-color: rgba(46, 204, 113, 0.12);
                }
                .modified-line {
                    background-color: rgba(241, 196, 15, 0.12);
                }
                .modified-line.deleted-part .line-content {
                    background-color: rgba(231, 76, 60, 0.12);
                }
                .modified-line.inserted-part .line-content {
                    background-color: rgba(46, 204, 113, 0.12);
                }
                .unchanged-line {
                    color: var(--text);
                }
                .deleted {
                    background-color: rgba(231, 76, 60, 0.3);
                    text-decoration: none;
                    border-radius: 2px;
                }
                .inserted {
                    background-color: rgba(46, 204, 113, 0.3);
                    text-decoration: none;
                    border-radius: 2px;
                }
                .empty-line {
                    background: repeating-linear-gradient(
                        45deg,
                        rgba(127, 140, 141, 0.1),
                        rgba(127, 140, 141, 0.1) 10px,
                        rgba(127, 140, 141, 0.03) 10px,
                        rgba(127, 140, 141, 0.03) 20px
                    );
                }
                /* Log styles */
                #log-toggle {
                    padding: 5px 10px;
                    background: var(--highlight);
                    color: white;
                    border: none;
                    border-radius: 4px;
                    cursor: pointer;
                    margin-top: 10px;
                }
                #log-content {
                    display: none;
                    margin-top: 10px;
                    max-height: 300px;
                    overflow: auto;
                    background: rgba(0,0,0,0.1);
                    padding: 10px;
                    border-radius: 4px;
                }
            ");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<div id=\"header\">");
            sb.AppendLine("<h1>Diff1cult - Relevant Source Change Report<sub style=\"font-size: 0.5em; margin-left: 8px;\"><a href='https://github.com/innominata/Diff1cult'>version 1.0 - by innominata</a>, grok3 and claude3.7</sub></h1>");
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
                function initializeResizers() {
                    // Initialize the diff pane resizer
                    const diffContainer = document.querySelector('.diff-container');
                    if (diffContainer) {
                        const resizer = diffContainer.querySelector('.diff-resizer');
                        if (resizer) {
                            // Make sure resizer is initially positioned at 50%
                            resizer.style.left = '50%';
                        }
                    }
                }
                
                // Call initializeResizers after page load and after selecting a method
                document.addEventListener('DOMContentLoaded', () => {
                    initializeResizers();
                });
                
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
                    
                    // Initialize the resizers after loading new content
                    setTimeout(initializeResizers, 10);
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
                        e.preventDefault();
                    });

                    document.addEventListener('mousemove', (e) => {
                        if (!isResizing) return;
                        
                        // Use pageX for more accurate position
                        const containerRect = document.getElementById('content').getBoundingClientRect();
                        const containerLeft = containerRect.left;
                        const containerWidth = containerRect.width;
                        
                        // Calculate the new width as pixels first, then convert to percentage
                        const newWidth = e.pageX - containerLeft;
                        const newWidthPercent = (newWidth / containerWidth) * 100;
                        
                        if (newWidthPercent >= 10 && newWidthPercent <= 50) {
                            leftPanel.style.width = `${newWidthPercent}%`;
                            rightPanel.style.width = `${100 - newWidthPercent}%`;
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
                    
                    if (!oldPane || !resizer || !newPane) return;
                    
                    let isResizing = false;
                    let startX;
                    let startOldWidth;
                    let startNewWidth;

                    resizer.addEventListener('mousedown', (e) => {
                        isResizing = true;
                        startX = e.clientX;
                        startOldWidth = oldPane.offsetWidth;
                        startNewWidth = newPane.offsetWidth;
                        document.body.style.cursor = 'col-resize';
                        resizer.classList.add('resizing');
                        e.preventDefault();
                    });

                    document.addEventListener('mousemove', (e) => {
                        if (!isResizing) return;
                        
                        const dx = e.clientX - startX;
                        const containerWidth = diffContainer.offsetWidth;
                        
                        // Calculate new widths
                        let newOldWidth = (startOldWidth + dx);
                        let newNewWidth = (startNewWidth - dx);
                        
                        // Convert to percentages
                        const oldWidthPercent = (newOldWidth / containerWidth) * 100;
                        const newWidthPercent = (newNewWidth / containerWidth) * 100;
                        
                        // Apply bounds (20% - 80%)
                        if (oldWidthPercent >= 20 && oldWidthPercent <= 80) {
                            oldPane.style.width = `${oldWidthPercent}%`;
                            newPane.style.width = `${100 - oldWidthPercent}%`;
                            resizer.style.left = `${oldWidthPercent}%`;
                        }
                    });

                    document.addEventListener('mouseup', () => {
                        if (isResizing) {
                            isResizing = false;
                            document.body.style.cursor = 'default';
                            resizer.classList.remove('resizing');
                        }
                    });

                    // Set initial position
                    oldPane.style.width = '50%';
                    newPane.style.width = '50%';
                    resizer.style.left = '50%';
                }

                var toggleBtn = document.getElementById('log-toggle');
                var logContent = document.getElementById('log-content');
                // Log is collapsed by default
                logContent.style.display = 'none';
                toggleBtn.textContent = 'Show Log';
                
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

                document.querySelectorAll('#left-panel li').forEach(li => {
                    li.addEventListener('click', () => selectMethod(li.dataset.id));
                });
                
                document.querySelectorAll('.tab-button').forEach(btn => {
                    btn.addEventListener('click', () => showTab(btn.dataset.tab));
                });
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
    // This is a completely different comment
    int value = 42;
    string name = ""oldName"";
    
    // This line will be deleted
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
    // This comment has nothing in common with the old one at all
    int value = 100;
    string name = ""newName"";
    
    // This is a new line that will be added
    Console.WriteLine(""This is a brand new line"");
    
    // This line will stay the same
    bool isEnabled = true;
    
    // Common code with some changes
    if (isEnabled) 
    {
        DoSomething(""new parameter"");
    }
}";

            string patchCode = @"[HarmonyPatch(typeof(GameMain), nameof(GameMain.End))]
public static class End
{
    static void Postfix()
    {
        // Patch implementation
        Console.WriteLine(""Patch applied!"");
    }
}";

            // Generate the diff HTML
            string diffHtml = GenerateDiffHtml(oldCode, newCode);
            
            // Return the example
            return ("test-diff", "Test Diff Example", diffHtml, HtmlEncode(oldCode), HtmlEncode(newCode), HtmlEncode(patchCode));
        }

        // Test method to verify inline diffing
        static void TestInlineDiff()
        {
            if (verboseMode)
            {
                Console.WriteLine("======= TESTING INLINE DIFF =======");
                
                // Test case 1: Simple parameter change
                string result1 = ExplicitSimpleDiff("DoSomething(10, true)", "DoSomething(20, true)", true);
                Console.WriteLine("\nTest case 1: Simple value change");
                Console.WriteLine($"HTML Result: {result1}");
                
                // Test case 2: Multiple parameter changes
                string result2 = ExplicitSimpleDiff(
                    @"SetValues(name: ""John"", age: 30, active: true)", 
                    @"SetValues(name: ""Jane"", age: 25, active: false)", 
                    true
                );
                Console.WriteLine("\nTest case 2: Multiple value changes");
                Console.WriteLine($"HTML Result: {result2}");
                
                Console.WriteLine("\nTesting full diff HTML generation");
                var html = GenerateDiffHtml(
                    "public void TestMethod()\n{\n    int value = 42;\n    Console.WriteLine(value);\n}",
                    "public void TestMethod()\n{\n    int value = 100;\n    Console.WriteLine(value);\n}"
                );
                Console.WriteLine($"Full HTML Result:\n{html}");
                Console.WriteLine("======= END TESTING INLINE DIFF =======");
            }
        }

        // Very direct, simple diffing that focuses on literal values
        static string ExplicitSimpleDiff(string oldLine, string newLine, bool isOldVersion)
        {
            if (verboseMode)
            {
                Log($"DEBUG: ExplicitSimpleDiff called with:");
                Log($"DEBUG:   oldLine: '{oldLine}'");
                Log($"DEBUG:   newLine: '{newLine}'");
                Log($"DEBUG:   isOldVersion: {isOldVersion}");
            }
            
            // Split by common code delimiters
            string[] delimiters = new[] { " ", "\t", "(", ")", "[", "]", "{", "}", ";", ",", ".", ":", "=", "+", "-", "*", "/", "<", ">", "&&", "||" };
            
            // Manual pattern for finding literals like "42", "100", etc.
            var numberPattern = @"\b\d+\b";
            var stringPattern = @"""[^""]*""";
            var literalPattern = $"({numberPattern}|{stringPattern})";
            
            var oldMatches = Regex.Matches(oldLine, literalPattern);
            var newMatches = Regex.Matches(newLine, literalPattern);
            
            // Log regex matches only in verbose mode
            if (verboseMode)
            {
                Log($"DEBUG:   Found {oldMatches.Count} literals in oldLine:");
                for (int i = 0; i < oldMatches.Count; i++)
                {
                    Log($"DEBUG:     [{i}] '{oldMatches[i].Value}' at position {oldMatches[i].Index}");
                }
                
                Log($"DEBUG:   Found {newMatches.Count} literals in newLine:");
                for (int i = 0; i < newMatches.Count; i++)
                {
                    Log($"DEBUG:     [{i}] '{newMatches[i].Value}' at position {newMatches[i].Index}");
                }
            }
            
            // If we can't find literals, just return encoded text
            if ((oldMatches.Count == 0 && newMatches.Count == 0) || oldLine == newLine)
            {
                if (verboseMode)
                {
                    Log("DEBUG:   No literals found or lines are identical - returning full line");
                }
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
                        Log($"DEBUG:     Difference found at index {i}: '{oldMatches[i].Value}' vs '{newMatches[i].Value}'");
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
                    Log($"DEBUG:   Single literal difference found - returning with highlighted '{value}'");
                    return $"{HtmlEncode(before)}<span class=\"{cssClass}\">{HtmlEncode(value)}</span>{HtmlEncode(after)}";
                }
                else 
                {
                    Log($"DEBUG:   Found {diffIndices.Count} differences - not a single literal change");
                }
            }
            
            // Special case for "value = 42" -> "value = 100"
            var valuePatternOld = Regex.Match(oldLine, @"(\w+\s*=\s*)(\d+|\""[^""]*\"")(.*)");
            var valuePatternNew = Regex.Match(newLine, @"(\w+\s*=\s*)(\d+|\""[^""]*\"")(.*)");
            
            Log("DEBUG:   Checking for assignment pattern");
            Log($"DEBUG:   Old match success: {valuePatternOld.Success}, Groups: {valuePatternOld.Groups.Count}");
            if (valuePatternOld.Success)
            {
                for (int i = 0; i < valuePatternOld.Groups.Count; i++)
                {
                    Log($"DEBUG:     Group[{i}]: '{valuePatternOld.Groups[i].Value}'");
                }
            }
            
            Log($"DEBUG:   New match success: {valuePatternNew.Success}, Groups: {valuePatternNew.Groups.Count}");
            if (valuePatternNew.Success)
            {
                for (int i = 0; i < valuePatternNew.Groups.Count; i++)
                {
                    Log($"DEBUG:     Group[{i}]: '{valuePatternNew.Groups[i].Value}'");
                }
            }
            
            if (valuePatternOld.Success && valuePatternNew.Success && 
                valuePatternOld.Groups[1].Value == valuePatternNew.Groups[1].Value &&
                valuePatternOld.Groups[3].Value == valuePatternNew.Groups[3].Value)
            {
                // This is the exact case we're looking for - assignment with different values
                Log("DEBUG:   Found exact assignment pattern with different values!");
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
            Log("DEBUG:   No special case found - returning whole line");
            return HtmlEncode(isOldVersion ? oldLine : newLine);
        }
    }
}