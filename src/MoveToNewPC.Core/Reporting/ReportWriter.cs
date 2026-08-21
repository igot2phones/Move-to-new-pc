using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MoveToNewPC.Core.Diagnostics;
using MoveToNewPC.Core.Model;
using MoveToNewPC.Core.Util;

namespace MoveToNewPC.Core.Reporting
{
    /// <summary>
    /// Writes the report the operator keeps and (eventually) sends to whoever is
    /// supporting them. Every skipped and failed item appears with its reason - a report
    /// that says "done!" while quietly having dropped 400 files is the failure mode this
    /// whole application is built to avoid.
    /// </summary>
    public static class ReportWriter
    {
        /// <summary>Rows written to the detail table before it is truncated with a pointer to the log.</summary>
        private const int MaxRows = 2000;

        public static string BuildText(TransferReport report)
        {
            StringBuilder sb = new StringBuilder(8192);

            sb.AppendLine("=======================================================================");
            sb.AppendLine(" " + report.Title + " - transfer report");
            sb.AppendLine("=======================================================================");
            sb.AppendLine();
            sb.AppendLine("Mode:              " + Safe(report.Mode));
            sb.AppendLine("From:              " + Safe(report.SourceMachine));
            sb.AppendLine("To:                " + Safe(report.DestinationMachine));
            if (!string.IsNullOrEmpty(report.DestinationDescription))
            {
                sb.AppendLine("Destination:       " + report.DestinationDescription);
            }
            sb.AppendLine("Started (UTC):     " + report.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("Finished (UTC):    " + report.FinishedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("Duration:          " + Format.Duration(report.Duration));
            sb.AppendLine("Average speed:     " + Format.Rate(report.AverageBytesPerSecond));
            sb.AppendLine();

            if (report.Cancelled)
            {
                sb.AppendLine("*** THIS TRANSFER WAS CANCELLED BEFORE IT FINISHED ***");
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(report.FailureMessage))
            {
                sb.AppendLine("*** THE TRANSFER STOPPED WITH AN ERROR ***");
                sb.AppendLine(report.FailureMessage);
                sb.AppendLine();
            }

            sb.AppendLine("-- Totals -------------------------------------------------------------");
            sb.AppendLine("Files copied:      " + report.FilesCopied.ToString("N0", CultureInfo.CurrentCulture));
            sb.AppendLine("Files skipped:     " + report.FilesSkipped.ToString("N0", CultureInfo.CurrentCulture));
            sb.AppendLine("Files failed:      " + report.FilesFailed.ToString("N0", CultureInfo.CurrentCulture));
            sb.AppendLine("Folders created:   " + report.DirectoriesCreated.ToString("N0", CultureInfo.CurrentCulture));
            sb.AppendLine("Bytes copied:      " + Format.Bytes(report.BytesCopied)
                          + " (" + report.BytesCopied.ToString("N0", CultureInfo.CurrentCulture) + ")");
            sb.AppendLine("Bytes not copied:  " + Format.Bytes(report.BytesSkipped));
            sb.AppendLine();

            if (report.Notes.Count > 0)
            {
                sb.AppendLine("-- Notes --------------------------------------------------------------");
                for (int i = 0; i < report.Notes.Count; i++)
                {
                    sb.AppendLine(" * " + report.Notes[i]);
                }
                sb.AppendLine();
            }

            Dictionary<SkipReason, int> byReason = GroupByReason(report.Skipped);
            if (byReason.Count > 0)
            {
                sb.AppendLine("-- Why things were not copied -----------------------------------------");
                foreach (KeyValuePair<SkipReason, int> pair in byReason)
                {
                    sb.AppendLine("  " + pair.Value.ToString("N0", CultureInfo.CurrentCulture).PadLeft(9)
                                  + "  " + SkipReasons.Describe(pair.Key));
                }
                sb.AppendLine();
            }

            if (report.Skipped.Count > 0)
            {
                sb.AppendLine("-- Skipped and failed items -------------------------------------------");
                int rows = Math.Min(report.Skipped.Count, MaxRows);
                for (int i = 0; i < rows; i++)
                {
                    SkippedItem item = report.Skipped[i];
                    sb.AppendLine((SkipReasons.IsFailure(item.Reason) ? "FAILED  " : "skipped ")
                                  + SkipReasons.Describe(item.Reason));
                    sb.AppendLine("        " + item.Path);
                    if (!string.IsNullOrEmpty(item.Detail))
                    {
                        sb.AppendLine("        " + item.Detail);
                    }
                }
                if (report.Skipped.Count > rows)
                {
                    sb.AppendLine();
                    sb.AppendLine("  ... and " + (report.Skipped.Count - rows).ToString("N0", CultureInfo.CurrentCulture)
                                  + " more. The full list is in the log file.");
                }
                sb.AppendLine();
            }

            sb.AppendLine("-- Notes on what is deliberately NOT copied ---------------------------");
            sb.AppendLine(" * File permissions (ACLs) and ownership are not copied. The user and");
            sb.AppendLine("   group IDs from the old PC mean nothing on the new one, and copying");
            sb.AppendLine("   them would leave files nobody can open. The destination folder's own");
            sb.AppendLine("   inherited permissions apply instead.");
            sb.AppendLine(" * Alternate data streams are ignored (they are almost always the");
            sb.AppendLine("   \"downloaded from the internet\" marker).");
            sb.AppendLine(" * Junctions and symbolic links are recorded but not recreated.");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(report.LogFilePath))
            {
                sb.AppendLine("Log file: " + report.LogFilePath);
            }

            return sb.ToString();
        }

        public static string BuildHtml(TransferReport report)
        {
            StringBuilder sb = new StringBuilder(16384);

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset=\"utf-8\" />");
            sb.AppendLine("<title>" + Html(report.Title) + " - transfer report</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Tahoma,sans-serif;font-size:13px;margin:24px;color:#222;}");
            sb.AppendLine("h1{font-size:20px;margin:0 0 4px 0;} h2{font-size:15px;margin:24px 0 8px 0;}");
            sb.AppendLine("table{border-collapse:collapse;margin-top:8px;} td,th{padding:3px 10px;text-align:left;");
            sb.AppendLine("border-bottom:1px solid #ddd;vertical-align:top;} th{background:#f2f2f2;}");
            sb.AppendLine(".num{text-align:right;font-variant-numeric:tabular-nums;}");
            sb.AppendLine(".fail{color:#a80000;font-weight:bold;} .skip{color:#7a5b00;}");
            sb.AppendLine(".path{font-family:Consolas,monospace;font-size:12px;word-break:break-all;}");
            sb.AppendLine(".warn{background:#fff4ce;border:1px solid #e6c200;padding:8px 12px;margin:12px 0;}");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine("<h1>" + Html(report.Title) + "</h1>");
            sb.AppendLine("<div>" + Html(report.Mode) + "</div>");

            if (report.Cancelled)
            {
                sb.AppendLine("<div class=\"warn\"><b>This transfer was cancelled before it finished.</b></div>");
            }
            if (!string.IsNullOrEmpty(report.FailureMessage))
            {
                sb.AppendLine("<div class=\"warn\"><b>The transfer stopped with an error:</b><br />"
                              + Html(report.FailureMessage).Replace("\n", "<br />") + "</div>");
            }

            sb.AppendLine("<h2>Summary</h2><table>");
            Row(sb, "From", report.SourceMachine);
            Row(sb, "To", report.DestinationMachine);
            if (!string.IsNullOrEmpty(report.DestinationDescription))
            {
                Row(sb, "Destination", report.DestinationDescription);
            }
            Row(sb, "Started (UTC)", report.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Row(sb, "Duration", Format.Duration(report.Duration));
            Row(sb, "Average speed", Format.Rate(report.AverageBytesPerSecond));
            Row(sb, "Files copied", report.FilesCopied.ToString("N0", CultureInfo.CurrentCulture));
            Row(sb, "Files skipped", report.FilesSkipped.ToString("N0", CultureInfo.CurrentCulture));
            Row(sb, "Files failed", report.FilesFailed.ToString("N0", CultureInfo.CurrentCulture));
            Row(sb, "Folders created", report.DirectoriesCreated.ToString("N0", CultureInfo.CurrentCulture));
            Row(sb, "Bytes copied", Format.Bytes(report.BytesCopied));
            Row(sb, "Bytes not copied", Format.Bytes(report.BytesSkipped));
            sb.AppendLine("</table>");

            if (report.Notes.Count > 0)
            {
                sb.AppendLine("<h2>Notes</h2><ul>");
                for (int i = 0; i < report.Notes.Count; i++)
                {
                    sb.AppendLine("<li>" + Html(report.Notes[i]) + "</li>");
                }
                sb.AppendLine("</ul>");
            }

            Dictionary<SkipReason, int> byReason = GroupByReason(report.Skipped);
            if (byReason.Count > 0)
            {
                sb.AppendLine("<h2>Why things were not copied</h2><table><tr><th>Count</th><th>Reason</th></tr>");
                foreach (KeyValuePair<SkipReason, int> pair in byReason)
                {
                    sb.AppendLine("<tr><td class=\"num\">" + pair.Value.ToString("N0", CultureInfo.CurrentCulture)
                                  + "</td><td>" + Html(SkipReasons.Describe(pair.Key)) + "</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            if (report.Skipped.Count > 0)
            {
                sb.AppendLine("<h2>Skipped and failed items</h2>");
                sb.AppendLine("<table><tr><th>Status</th><th>Reason</th><th>Item</th><th>Details</th></tr>");
                int rows = Math.Min(report.Skipped.Count, MaxRows);
                for (int i = 0; i < rows; i++)
                {
                    SkippedItem item = report.Skipped[i];
                    bool failed = SkipReasons.IsFailure(item.Reason);
                    sb.AppendLine("<tr><td class=\"" + (failed ? "fail" : "skip") + "\">"
                                  + (failed ? "FAILED" : "skipped") + "</td><td>"
                                  + Html(SkipReasons.Describe(item.Reason)) + "</td><td class=\"path\">"
                                  + Html(item.Path) + "</td><td>" + Html(item.Detail) + "</td></tr>");
                }
                sb.AppendLine("</table>");
                if (report.Skipped.Count > rows)
                {
                    sb.AppendLine("<p>... and " + (report.Skipped.Count - rows).ToString("N0", CultureInfo.CurrentCulture)
                                  + " more. The full list is in the log file.</p>");
                }
            }

            sb.AppendLine("<h2>What is deliberately not copied</h2><ul>");
            sb.AppendLine("<li>File permissions (ACLs) and ownership. The account IDs from the old PC");
            sb.AppendLine("mean nothing on the new one; copying them would leave files nobody can open.</li>");
            sb.AppendLine("<li>Alternate data streams (almost always the \"downloaded from the internet\" marker).</li>");
            sb.AppendLine("<li>Junctions and symbolic links: recorded, not recreated.</li>");
            sb.AppendLine("</ul>");

            if (!string.IsNullOrEmpty(report.LogFilePath))
            {
                sb.AppendLine("<p class=\"path\">Log file: " + Html(report.LogFilePath) + "</p>");
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>Writes both formats next to each other. Returns the .txt path.</summary>
        public static string Save(TransferReport report, string directory)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string textPath = Path.Combine(directory, "MoveToNewPC-report-" + stamp + ".txt");
            string htmlPath = Path.Combine(directory, "MoveToNewPC-report-" + stamp + ".html");

            try
            {
                File.WriteAllText(textPath, BuildText(report), new UTF8Encoding(true));
                File.WriteAllText(htmlPath, BuildHtml(report), new UTF8Encoding(true));
                Log.Info("Report written to " + textPath + " and " + htmlPath);
                return textPath;
            }
            catch (Exception ex)
            {
                Log.Error("Could not write the report to " + directory, ex);
                return null;
            }
        }

        private static Dictionary<SkipReason, int> GroupByReason(List<SkippedItem> items)
        {
            Dictionary<SkipReason, int> counts = new Dictionary<SkipReason, int>();
            for (int i = 0; i < items.Count; i++)
            {
                int existing;
                counts.TryGetValue(items[i].Reason, out existing);
                counts[items[i].Reason] = existing + 1;
            }
            return counts;
        }

        private static void Row(StringBuilder sb, string label, string value)
        {
            sb.AppendLine("<tr><th>" + Html(label) + "</th><td>" + Html(value) + "</td></tr>");
        }

        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "-" : value;
        }

        private static string Html(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(value.Length + 16);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
