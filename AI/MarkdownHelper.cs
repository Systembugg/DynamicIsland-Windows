using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DynamicIsland.AI
{
    public static class MarkdownHelper
    {
        private static readonly SolidColorBrush TextWhite = new SolidColorBrush(Color.FromRgb(245, 245, 247));
        private static readonly SolidColorBrush AccentBlue = new SolidColorBrush(Color.FromRgb(10, 132, 255));
        private static readonly SolidColorBrush SecondaryGray = new SolidColorBrush(Color.FromRgb(174, 174, 178));
        private static readonly SolidColorBrush CodeBg = new SolidColorBrush(Color.FromRgb(38, 38, 40));

        static MarkdownHelper()
        {
            TextWhite.Freeze();
            AccentBlue.Freeze();
            SecondaryGray.Freeze();
            CodeBg.Freeze();
        }

        public static void RenderMarkdown(TextBlock target, string rawMarkdown)
        {
            if (target == null) return;
            target.Inlines.Clear();

            if (string.IsNullOrWhiteSpace(rawMarkdown))
                return;

            // Strip any leftover XML/Action tags
            string cleaned = Regex.Replace(rawMarkdown, @"<(?:ACTION:[A-Z]+|MEMORY_[A-Z]+)\b[^>]*?(?:/>|>.*?</(?:ACTION:[A-Z]+|MEMORY_[A-Z]+)>|>)", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            cleaned = Regex.Replace(cleaned, @"</?(?:ACTION:[A-Z]+|MEMORY_[A-Z]+)[^>]*>", "", RegexOptions.IgnoreCase);
            cleaned = cleaned.Trim();

            var lines = cleaned.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (i > 0)
                {
                    target.Inlines.Add(new LineBreak());
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Header check: # or ## or ###
                var headerMatch = Regex.Match(line, @"^(#{1,4})\s+(.*)$");
                if (headerMatch.Success)
                {
                    string headerText = headerMatch.Groups[2].Value;
                    var run = new Run(headerText)
                    {
                        FontWeight = FontWeights.Bold,
                        Foreground = TextWhite,
                        FontSize = target.FontSize + 1.5
                    };
                    target.Inlines.Add(run);
                    continue;
                }

                // Bullet point check: * or -
                var bulletMatch = Regex.Match(line, @"^\s*[\*\-]\s+(.*)$");
                if (bulletMatch.Success)
                {
                    // Clean Apple-style bullet point
                    var bullet = new Run(" •  ")
                    {
                        Foreground = AccentBlue,
                        FontWeight = FontWeights.Bold
                    };
                    target.Inlines.Add(bullet);
                    AppendFormattedInlineSpans(target, bulletMatch.Groups[1].Value);
                    continue;
                }

                // Numbered list: 1. or 2.
                var numMatch = Regex.Match(line, @"^\s*(\d+\.)\s+(.*)$");
                if (numMatch.Success)
                {
                    var numRun = new Run($" {numMatch.Groups[1].Value} ")
                    {
                        Foreground = AccentBlue,
                        FontWeight = FontWeights.SemiBold
                    };
                    target.Inlines.Add(numRun);
                    AppendFormattedInlineSpans(target, numMatch.Groups[2].Value);
                    continue;
                }

                // Standard paragraph line
                AppendFormattedInlineSpans(target, line);
            }
        }

        private static void AppendFormattedInlineSpans(TextBlock target, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Pattern to match **bold**, *italic*, `code`
            var pattern = @"(\*\*(.*?)\*\*|\*(.*?)\*|`(.*?)`)";
            var matches = Regex.Matches(text, pattern);

            int lastIdx = 0;
            foreach (Match match in matches)
            {
                // Text before formatting
                if (match.Index > lastIdx)
                {
                    string plain = text.Substring(lastIdx, match.Index - lastIdx);
                    target.Inlines.Add(new Run(plain) { Foreground = TextWhite });
                }

                if (match.Value.StartsWith("**") && match.Value.EndsWith("**"))
                {
                    // Bold
                    string inner = match.Groups[2].Value;
                    var boldRun = new Run(inner)
                    {
                        FontWeight = FontWeights.Bold,
                        Foreground = TextWhite
                    };
                    target.Inlines.Add(boldRun);
                }
                else if (match.Value.StartsWith("`") && match.Value.EndsWith("`"))
                {
                    // Inline Code
                    string inner = match.Groups[4].Value;
                    var codeRun = new Run($" {inner} ")
                    {
                        FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                        Foreground = AccentBlue,
                        Background = CodeBg,
                        FontSize = target.FontSize - 0.5
                    };
                    target.Inlines.Add(codeRun);
                }
                else if (match.Value.StartsWith("*") && match.Value.EndsWith("*"))
                {
                    // Italic
                    string inner = match.Groups[3].Value;
                    var italicRun = new Run(inner)
                    {
                        FontStyle = FontStyles.Italic,
                        Foreground = SecondaryGray
                    };
                    target.Inlines.Add(italicRun);
                }

                lastIdx = match.Index + match.Length;
            }

            // Remaining text
            if (lastIdx < text.Length)
            {
                string remainder = text.Substring(lastIdx);
                target.Inlines.Add(new Run(remainder) { Foreground = TextWhite });
            }
        }
    }
}
