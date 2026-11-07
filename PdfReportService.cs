using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Drawing;

namespace DiagnoseTool
{
    public static class PdfReportService
    {
        public static void GenerateReport(
            string filePath,
            string cpuName,
            string gpuName,
            string ramTotalDisplay,
            SystemInfo systemInfo,
            DateTime startTime,
            DateTime endTime,
            List<LogDataPoint> dataPoints,
            string orderNumber = null)
        {
            // Create PDF Document
            var document = new PdfDocument();
            document.Info.Title = !string.IsNullOrEmpty(orderNumber)
                ? $"System Hardware Prüfbericht - Auftrag {orderNumber}"
                : "System Hardware Prüfbericht";
            document.Info.Author = "CPM Diagnose Tool";
            document.Info.Subject = "Hardware Diagnostics Log";

            // Add Page (DIN A4 size: 595 x 842 points)
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            var gfx = XGraphics.FromPdfPage(page);

            // --- Colors ---
            var colorPrimary = XColor.FromArgb(0, 129, 188); // CPM Blue
            var colorDark = XColor.FromArgb(30, 41, 59);     // Dark Slate
            var colorGray = XColor.FromArgb(100, 116, 139);   // Muted Slate
            var colorLightBg = XColor.FromArgb(245, 247, 250); // Light Gray Card
            var colorBorder = XColor.FromArgb(226, 232, 240); // Card border

            // --- Fonts ---
            var fontTitle = new XFont("Segoe UI", 22, XFontStyleEx.Bold);
            var fontSubtitle = new XFont("Segoe UI", 10, XFontStyleEx.Italic);
            var fontHeading = new XFont("Segoe UI", 13, XFontStyleEx.Bold);
            var fontSubHeading = new XFont("Segoe UI", 11, XFontStyleEx.Bold);
            var fontBody = new XFont("Segoe UI", 9.5, XFontStyleEx.Regular);
            var fontBodyBold = new XFont("Segoe UI", 9.5, XFontStyleEx.Bold);
            var fontMuted = new XFont("Segoe UI", 8, XFontStyleEx.Regular);

            // --- 1. Header (Title & CPM Logo) ---
            // Draw title text
            gfx.DrawString("SYSTEM-PRÜFBERICHT", fontTitle, new XSolidBrush(colorPrimary), 40, 60);
            gfx.DrawString("Hardware-Diagnose & Stresstest Protokoll", fontSubtitle, new XSolidBrush(colorGray), 40, 78);

            // Draw CPM Logo (Rendered from WPF Vector Paths in memory)
            try
            {
                byte[] logoBytes = GetCpmLogoPng(120, 43);
                using (var ms = new MemoryStream(logoBytes))
                {
                    var logoImage = XImage.FromStream(ms);
                    gfx.DrawImage(logoImage, 435, 40, 120, 43);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to draw logo: {ex.Message}");
                // Fallback text if logo rendering fails
                gfx.DrawString("CPM", new XFont("Segoe UI", 20, XFontStyleEx.Bold), new XSolidBrush(colorPrimary), 490, 60);
            }

            // Draw Divider Line
            var penDivider = new XPen(colorPrimary, 1.5);
            gfx.DrawLine(penDivider, 40, 95, 555, 95);

            // --- 2. Section: System- & Aufzeichnungsdaten ---
            // Draw background card
            var rectSystemBox = new XRect(40, 110, 515, 130);
            gfx.DrawRectangle(new XSolidBrush(colorLightBg), rectSystemBox);
            gfx.DrawRectangle(new XPen(colorBorder, 1), rectSystemBox);

            // Columns layout inside the card
            // Column 1: System Info
            gfx.DrawString("HARDWARE-KONFIGURATION", fontSubHeading, new XSolidBrush(colorPrimary), 52, 128);
            gfx.DrawString($"System-Modell:    {systemInfo.FullSystemName}", fontBody, new XSolidBrush(colorDark), 52, 144);
            gfx.DrawString($"Seriennummer:     {systemInfo.SerialNumber}", fontBody, new XSolidBrush(colorDark), 52, 158);
            gfx.DrawString($"Prozessor (CPU):  {cpuName}", fontBody, new XSolidBrush(colorDark), 52, 172);
            gfx.DrawString($"Grafikkarte (GPU): {gpuName}", fontBody, new XSolidBrush(colorDark), 52, 186);
            gfx.DrawString($"Arbeitsspeicher:  {ramTotalDisplay}", fontBody, new XSolidBrush(colorDark), 52, 200);
            gfx.DrawString($"Mainboard:        {systemInfo.MotherboardModel}", fontBody, new XSolidBrush(colorDark), 52, 214);

            // Column 2: Log Info
            var duration = endTime - startTime;
            gfx.DrawString("AUFZEICHNUNGSDATEN", fontSubHeading, new XSolidBrush(colorPrimary), 320, 128);
            if (!string.IsNullOrEmpty(orderNumber))
            {
                gfx.DrawString($"Auftrags-Nr.: {orderNumber}", fontBodyBold, new XSolidBrush(colorPrimary), 320, 144);
                gfx.DrawString($"Startzeit:    {startTime:dd.MM.yyyy HH:mm:ss}", fontBody, new XSolidBrush(colorDark), 320, 158);
                gfx.DrawString($"Endzeit:      {endTime:dd.MM.yyyy HH:mm:ss}", fontBody, new XSolidBrush(colorDark), 320, 172);
                gfx.DrawString($"Messdauer:    {duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2} ({dataPoints.Count} Punkte)", fontBody, new XSolidBrush(colorDark), 320, 186);
            }
            else
            {
                gfx.DrawString($"Startzeit:    {startTime:dd.MM.yyyy HH:mm:ss}", fontBody, new XSolidBrush(colorDark), 320, 144);
                gfx.DrawString($"Endzeit:      {endTime:dd.MM.yyyy HH:mm:ss}", fontBody, new XSolidBrush(colorDark), 320, 158);
                gfx.DrawString($"Messdauer:    {duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2} ({dataPoints.Count} Punkte)", fontBody, new XSolidBrush(colorDark), 320, 172);
            }

            // --- 3. Section: Zusammenfassung (Statistiken) ---
            gfx.DrawString("ZUSAMMENFASSUNG DER MESSWERTE", fontHeading, new XSolidBrush(colorDark), 40, 265);

            // Draw Summary Table Header
            double tableY = 280;
            double rowHeight = 22;
            var brushHeader = new XSolidBrush(XColor.FromArgb(241, 245, 249));
            gfx.DrawRectangle(brushHeader, 40, tableY, 515, rowHeight);
            gfx.DrawRectangle(new XPen(colorBorder, 1), 40, tableY, 515, rowHeight);

            // Columns headers
            gfx.DrawString("Komponente", fontBodyBold, new XSolidBrush(colorDark), 50, tableY + 14);
            gfx.DrawString("Ø Auslastung", fontBodyBold, new XSolidBrush(colorDark), 180, tableY + 14);
            gfx.DrawString("Max. Auslastung", fontBodyBold, new XSolidBrush(colorDark), 280, tableY + 14);
            gfx.DrawString("Ø Temp.", fontBodyBold, new XSolidBrush(colorDark), 380, tableY + 14);
            gfx.DrawString("Max. Temp.", fontBodyBold, new XSolidBrush(colorDark), 470, tableY + 14);

            // Calculate Metrics
            var cpuLoads = dataPoints.Where(p => p.CpuCpuLoad.HasValue).Select(p => p.CpuCpuLoad.Value).ToList();
            float? avgCpuLoad = cpuLoads.Count > 0 ? cpuLoads.Average() : (float?)null;
            float? maxCpuLoad = cpuLoads.Count > 0 ? cpuLoads.Max() : (float?)null;

            var cpuTemps = dataPoints.Where(p => p.CpuCpuTemp.HasValue && p.CpuCpuTemp.Value > 0).Select(p => p.CpuCpuTemp.Value).ToList();
            float? avgCpuTemp = cpuTemps.Count > 0 ? cpuTemps.Average() : (float?)null;
            float? maxCpuTemp = cpuTemps.Count > 0 ? cpuTemps.Max() : (float?)null;

            var gpuLoads = dataPoints.Where(p => p.GpuGpuLoad.HasValue).Select(p => p.GpuGpuLoad.Value).ToList();
            float? avgGpuLoad = gpuLoads.Count > 0 ? (gpuName.Contains("Keine") ? (float?)null : gpuLoads.Average()) : (float?)null;
            float? maxGpuLoad = gpuLoads.Count > 0 ? (gpuName.Contains("Keine") ? (float?)null : gpuLoads.Max()) : (float?)null;

            var gpuTemps = dataPoints.Where(p => p.GpuGpuTemp.HasValue && p.GpuGpuTemp.Value > 0).Select(p => p.GpuGpuTemp.Value).ToList();
            float? avgGpuTemp = gpuTemps.Count > 0 ? (gpuName.Contains("Keine") ? (float?)null : gpuTemps.Average()) : (float?)null;
            float? maxGpuTemp = gpuTemps.Count > 0 ? (gpuName.Contains("Keine") ? (float?)null : gpuTemps.Max()) : (float?)null;

            var ramPercentList = dataPoints.Where(p => p.RamPercent.HasValue).Select(p => p.RamPercent.Value).ToList();
            float? avgRamPercent = ramPercentList.Count > 0 ? ramPercentList.Average() : (float?)null;
            float? maxRamPercent = ramPercentList.Count > 0 ? ramPercentList.Max() : (float?)null;

            string cpuAvgLoadStr = avgCpuLoad.HasValue ? $"{avgCpuLoad.Value:F1} %" : "--";
            string cpuMaxLoadStr = maxCpuLoad.HasValue ? $"{maxCpuLoad.Value:F1} %" : "--";
            string cpuAvgTempStr = avgCpuTemp.HasValue ? $"{avgCpuTemp.Value:F1} °C" : "--";
            string cpuMaxTempStr = maxCpuTemp.HasValue ? $"{maxCpuTemp.Value:F1} °C" : "--";

            string gpuAvgLoadStr = avgGpuLoad.HasValue ? $"{avgGpuLoad.Value:F1} %" : "--";
            string gpuMaxLoadStr = maxGpuLoad.HasValue ? $"{maxGpuLoad.Value:F1} %" : "--";
            string gpuAvgTempStr = avgGpuTemp.HasValue ? $"{avgGpuTemp.Value:F1} °C" : "--";
            string gpuMaxTempStr = maxGpuTemp.HasValue ? $"{maxGpuTemp.Value:F1} °C" : "--";

            string ramAvgPercentStr = avgRamPercent.HasValue ? $"{avgRamPercent.Value:F1} %" : "--";
            string ramMaxPercentStr = maxRamPercent.HasValue ? $"{maxRamPercent.Value:F1} %" : "--";

            // Draw Rows
            DrawSummaryRow(gfx, fontBody, fontBodyBold, colorDark, colorBorder, "Prozessor (CPU)", cpuAvgLoadStr, cpuMaxLoadStr, cpuAvgTempStr, cpuMaxTempStr, tableY + rowHeight, rowHeight);
            DrawSummaryRow(gfx, fontBody, fontBodyBold, colorDark, colorBorder, "Grafikkarte (GPU)", gpuAvgLoadStr, gpuMaxLoadStr, gpuAvgTempStr, gpuMaxTempStr, tableY + (rowHeight * 2), rowHeight);
            DrawSummaryRow(gfx, fontBody, fontBodyBold, colorDark, colorBorder, "Arbeitsspeicher (RAM)", ramAvgPercentStr, ramMaxPercentStr, "--", "--", tableY + (rowHeight * 3), rowHeight);

            // --- 4. Section: Messwertverlauf (Timeline Table) ---
            gfx.DrawString("ZEITLICHER VERLAUF DER MESSWERTE", fontHeading, new XSolidBrush(colorDark), 40, 395);

            // Draw History Table Header
            double histY = 410;
            gfx.DrawRectangle(brushHeader, 40, histY, 515, rowHeight);
            gfx.DrawRectangle(new XPen(colorBorder, 1), 40, histY, 515, rowHeight);

            gfx.DrawString("Zeitstempel", fontBodyBold, new XSolidBrush(colorDark), 50, histY + 14);
            gfx.DrawString("CPU Last", fontBodyBold, new XSolidBrush(colorDark), 150, histY + 14);
            gfx.DrawString("CPU Temp.", fontBodyBold, new XSolidBrush(colorDark), 230, histY + 14);
            gfx.DrawString("GPU Last", fontBodyBold, new XSolidBrush(colorDark), 320, histY + 14);
            gfx.DrawString("GPU Temp.", fontBodyBold, new XSolidBrush(colorDark), 400, histY + 14);
            gfx.DrawString("RAM Last", fontBodyBold, new XSolidBrush(colorDark), 480, histY + 14);

            // Downsample list to fit up to 15 entries on the A4 page
            var displayList = new List<LogDataPoint>();
            if (dataPoints.Count <= 15)
            {
                displayList.AddRange(dataPoints);
            }
            else
            {
                // Always include start
                displayList.Add(dataPoints[0]);
                
                // Add evenly spaced internal items
                double step = (double)(dataPoints.Count - 1) / 14;
                for (int idx = 1; idx < 14; idx++)
                {
                    int targetIdx = (int)Math.Round(idx * step);
                    if (targetIdx > 0 && targetIdx < dataPoints.Count - 1)
                    {
                        displayList.Add(dataPoints[targetIdx]);
                    }
                }
                
                // Always include end
                displayList.Add(dataPoints.Last());
            }

            // Draw Rows of History
            double currentY = histY + rowHeight;
            bool isAltRow = false;
            var brushAltRow = new XSolidBrush(XColor.FromArgb(250, 251, 252));
            var brushWhite = new XSolidBrush(XColor.FromArgb(255, 255, 255));

            foreach (var point in displayList)
            {
                // Alternating background
                gfx.DrawRectangle(isAltRow ? brushAltRow : brushWhite, 40, currentY, 515, rowHeight);
                gfx.DrawRectangle(new XPen(colorBorder, 1), 40, currentY, 515, rowHeight);

                var timeStr = point.Timestamp.ToString("HH:mm:ss");
                gfx.DrawString(timeStr, fontBody, new XSolidBrush(colorDark), 50, currentY + 14);
                
                string cpuLoadStr = point.CpuCpuLoad.HasValue ? $"{point.CpuCpuLoad.Value:F1} %" : "--";
                gfx.DrawString(cpuLoadStr, fontBody, new XSolidBrush(colorDark), 150, currentY + 14);
                
                // CPU Temp Color alert
                string cpuTempStr = point.CpuCpuTemp.HasValue && point.CpuCpuTemp.Value > 0 ? $"{point.CpuCpuTemp.Value:F1} °C" : "--";
                var cpuBrush = new XSolidBrush(point.CpuCpuTemp.HasValue && point.CpuCpuTemp.Value > 80 ? XColor.FromArgb(239, 68, 68) : (point.CpuCpuTemp.HasValue && point.CpuCpuTemp.Value > 65 ? XColor.FromArgb(245, 158, 11) : colorDark));
                gfx.DrawString(cpuTempStr, fontBody, cpuBrush, 230, currentY + 14);
                
                string gpuLoadStr = point.GpuGpuLoad.HasValue && !gpuName.Contains("Keine") ? $"{point.GpuGpuLoad.Value:F1} %" : "--";
                gfx.DrawString(gpuLoadStr, fontBody, new XSolidBrush(colorDark), 320, currentY + 14);
                
                // GPU Temp Color alert
                string gpuTempStr = point.GpuGpuTemp.HasValue && point.GpuGpuTemp.Value > 0 && !gpuName.Contains("Keine") ? $"{point.GpuGpuTemp.Value:F1} °C" : "--";
                var gpuBrush = new XSolidBrush(point.GpuGpuTemp.HasValue && point.GpuGpuTemp.Value > 83 ? XColor.FromArgb(239, 68, 68) : (point.GpuGpuTemp.HasValue && point.GpuGpuTemp.Value > 75 ? XColor.FromArgb(245, 158, 11) : colorDark));
                gfx.DrawString(gpuTempStr, fontBody, gpuBrush, 400, currentY + 14);
                
                string ramPercentStr = point.RamPercent.HasValue ? $"{point.RamPercent.Value:F1} %" : "--";
                gfx.DrawString(ramPercentStr, fontBody, new XSolidBrush(colorDark), 480, currentY + 14);

                currentY += rowHeight;
                isAltRow = !isAltRow;
            }

            // --- 5. Footer ---
            gfx.DrawLine(new XPen(colorBorder, 1), 40, 800, 555, 800);
            gfx.DrawString("Generiert mit CPM Hardware-Diagnose Tool", fontMuted, new XSolidBrush(colorGray), 40, 814);
            gfx.DrawString("Seite 1 von 1", fontMuted, new XSolidBrush(colorGray), 510, 814);

            // Save PDF
            document.Save(filePath);
        }

        private static void DrawSummaryRow(
            XGraphics gfx,
            XFont font,
            XFont fontBold,
            XColor textColor,
            XColor borderColor,
            string label,
            string avgLoad,
            string maxLoad,
            string avgTemp,
            string maxTemp,
            double y,
            double height)
        {
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 255, 255)), 40, y, 515, height);
            gfx.DrawRectangle(new XPen(borderColor, 1), 40, y, 515, height);

            gfx.DrawString(label, fontBold, new XSolidBrush(textColor), 50, y + 14);
            gfx.DrawString(avgLoad, font, new XSolidBrush(textColor), 180, y + 14);
            gfx.DrawString(maxLoad, font, new XSolidBrush(textColor), 280, y + 14);
            gfx.DrawString(avgTemp, font, new XSolidBrush(textColor), 380, y + 14);
            gfx.DrawString(maxTemp, font, new XSolidBrush(textColor), 470, y + 14);
        }

        /// <summary>
        /// Generates a PNG representation of the CPM Vector Logo from paths in memory.
        /// </summary>
        private static byte[] GetCpmLogoPng(int width, int height)
        {
            return Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                var viewbox = new Viewbox { Width = width, Height = height };
                var canvas = new Canvas { Width = 119.11, Height = 42.75 };
                viewbox.Child = canvas;

                var brushTop = new SolidColorBrush(Color.FromRgb(26, 23, 27));
                var brushBottom = new SolidColorBrush(Color.FromRgb(0, 129, 188));

                canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Fill = brushTop, Data = StreamGeometry.Parse("M16.68,11c.23-.45,.58-.84,1-1.12,.38-.3,.86-.46,1.34-.45l15.71,.17L37.36,.24,16.38,0c-1.68-.04-3.33,.39-4.77,1.26-1.46,.9-2.73,2.06-3.76,3.43-1.14,1.52-2.09,3.17-2.83,4.92-.82,1.85-1.53,3.74-2.14,5.66,.01,.06,.01,.11,0,.17l11.76,.14c.6-1.56,1.28-3.09,2.04-4.58Z") });
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Fill = brushBottom, Data = StreamGeometry.Parse("M13.16,32.09c-.47-.12-.89-.38-1.21-.75-.32-.37-.46-.86-.38-1.34,.31-2.93,.9-5.81,1.75-8.63,.36-1.15,.76-2.27,1.19-3.38l-11.75-.11c-.58,1.87-1.07,3.73-1.46,5.56-.38,1.83-.74,3.56-1,5C.05,29.85-.05,31.29,.02,32.72c.06,1.44,.33,2.86,.81,4.21,.42,1.26,1.13,2.4,2.07,3.34,.98,.97,2.27,1.58,3.65,1.73,6.58,.88,13.25,.8,19.8-.24l2.67-9.76c-2.59,.53-5.23,.79-7.87,.78-2.66-.02-5.32-.26-7.94-.71l-.05,.02Z") });
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Fill = brushTop, Data = StreamGeometry.Parse("M50.93,7.64l6.6,.07c.42,0,.83,.13,1.18,.36,.36,.24,.66,.54,.9,.9,.25,.38,.44,.79,.57,1.23,.13,.42,.2,.87,.2,1.31,0,1.19-.21,2.37-.63,3.49-.15,.4-.33,.79-.54,1.16l10.07,.12c0-.17,.12-.34,.17-.51,.55-1.98,.85-4.02,.88-6.08,.01-1.04-.15-2.07-.48-3.05-.31-.96-.8-1.86-1.44-2.64-1-1.16-2.27-2.04-3.71-2.56-1.41-.51-2.89-.78-4.38-.8l-18-.2-4.4,15.42,10.59,.14,2.42-8.36Z") });
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Fill = brushBottom, Data = StreamGeometry.Parse("M59.2,18.53c-.33,.62-.72,1.21-1.18,1.75-.29,.34-.57,.67-.84,1-.29,.33-.63,.61-1,.84-.46,.26-.95,.45-1.46,.56-.72,.14-1.46,.2-2.19,.18l-5.31-.06,1.29-4.42-10.59-.12-6.76,23.59,10.59,.15,3.21-11.12,12.25,.12c1.03,.02,2.06-.19,3-.62,.95-.42,1.84-.96,2.65-1.62,.85-.69,1.62-1.48,2.3-2.34,.67-.86,1.29-1.76,1.86-2.7,.94-1.6,1.69-3.3,2.23-5.07l-10.05-.12Z") });
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Fill = brushBottom, Data = StreamGeometry.Parse("M67.7,42.28l10.5,.12,6.7-23.57-10.5-.13-6.7,23.58Z") });
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Fill = brushBottom, Data = StreamGeometry.Parse("M88.92,34h2.31l11.46-15-16.23-.19,2.46,15.19Z") });
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Fill = brushBottom, Data = StreamGeometry.Parse("M104.15,19.05l-6.77,23.57,10.64,.13,6.68-23.58-10.55-.12Z") });
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Fill = brushTop, Data = StreamGeometry.Parse("M119.11,1.33l-9.55-.11c-1.26,0-2.49,.31-3.59,.92-1.18,.65-2.2,1.54-3,2.63l-6.95,9.37-1.43-9c-.11-.6-.34-1.18-.68-1.69-.66-1-1.62-1.76-2.75-2.16-.55-.19-1.13-.29-1.71-.29l-10.64-.14-4.39,15.43,10.5,.12,1-3.53,.57,3.56,16.23,.19,2.32-3-.87,3.07,10.56,.12,4.38-15.49Z") });

                viewbox.Measure(new Size(width, height));
                viewbox.Arrange(new Rect(0, 0, width, height));

                int dpi = 300;
                double scale = dpi / 96.0;
                int renderWidth = (int)(width * scale);
                int renderHeight = (int)(height * scale);

                var rtb = new RenderTargetBitmap(new PixelSize(renderWidth, renderHeight), new Vector(dpi, dpi));
                rtb.Render(viewbox);

                using (var ms = new MemoryStream())
                {
                    rtb.Save(ms);
                    return ms.ToArray();
                }
            });
        }

        public static void GenerateBatteryReport(
            string filePath,
            string cpuName,
            string gpuName,
            string ramTotalDisplay,
            SystemInfo systemInfo,
            BatteryInfo batteryInfo,
            DateTime startTime,
            DateTime endTime,
            double startPercent,
            double endPercent,
            System.Collections.Generic.List<string> logEntries)
        {
            // Create PDF Document
            var document = new PdfDocument();
            document.Info.Title = "Batterie-Prüfbericht";
            document.Info.Author = "CPM Diagnose Tool";
            document.Info.Subject = "Battery stress test and health diagnostics";

            // Add Page (DIN A4 size: 595 x 842 points)
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            var gfx = XGraphics.FromPdfPage(page);

            // --- Colors ---
            var colorPrimary = XColor.FromArgb(16, 185, 129); // Battery green color (matching visual identity)
            var colorDark = XColor.FromArgb(30, 41, 59);     // Dark Slate
            var colorGray = XColor.FromArgb(100, 116, 139);   // Muted Slate
            var colorLightBg = XColor.FromArgb(245, 247, 250); // Light Gray Card
            var colorBorder = XColor.FromArgb(226, 232, 240); // Card border

            // --- Fonts ---
            var fontTitle = new XFont("Segoe UI", 20, XFontStyleEx.Bold);
            var fontSubtitle = new XFont("Segoe UI", 10, XFontStyleEx.Italic);
            var fontHeading = new XFont("Segoe UI", 12, XFontStyleEx.Bold);
            var fontSubHeading = new XFont("Segoe UI", 10.5, XFontStyleEx.Bold);
            var fontBody = new XFont("Segoe UI", 9, XFontStyleEx.Regular);
            var fontBodyBold = new XFont("Segoe UI", 9, XFontStyleEx.Bold);
            var fontMuted = new XFont("Segoe UI", 8, XFontStyleEx.Regular);

            // --- 1. Header (Title & CPM Logo) ---
            gfx.DrawString("BATTERIE-PRÜFBERICHT", fontTitle, new XSolidBrush(colorPrimary), 40, 60);
            gfx.DrawString("Batterie-Gesundheitsdiagnose & Stresstest Protokoll", fontSubtitle, new XSolidBrush(colorGray), 40, 76);

            // Draw CPM Logo
            try
            {
                byte[] logoBytes = GetCpmLogoPng(120, 43);
                using (var ms = new MemoryStream(logoBytes))
                {
                    var logoImage = XImage.FromStream(ms);
                    gfx.DrawImage(logoImage, 435, 40, 120, 43);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to draw logo: {ex.Message}");
                gfx.DrawString("CPM", new XFont("Segoe UI", 18, XFontStyleEx.Bold), new XSolidBrush(colorPrimary), 490, 60);
            }

            // Draw Divider Line
            var penDivider = new XPen(colorPrimary, 1.5);
            gfx.DrawLine(penDivider, 40, 95, 555, 95);

            // --- 2. Section: System- & Batterie-Daten ---
            // Draw background card for static info
            var rectSystemBox = new XRect(40, 110, 515, 135);
            gfx.DrawRectangle(new XSolidBrush(colorLightBg), rectSystemBox);
            gfx.DrawRectangle(new XPen(colorBorder, 1), rectSystemBox);

            // Columns layout inside the card
            // Column 1: Battery Health Specifications
            gfx.DrawString("BATTERIE-SPEZIFIKATIONEN", fontSubHeading, new XSolidBrush(colorPrimary), 52, 128);
            gfx.DrawString($"Gesundheitszustand (Health): {batteryInfo.HealthDisplay}", fontBodyBold, new XSolidBrush(colorDark), 52, 142);
            gfx.DrawString($"Hersteller / Modell:         {batteryInfo.Manufacturer} / {batteryInfo.DeviceName}", fontBody, new XSolidBrush(colorDark), 52, 154);
            gfx.DrawString($"Design-Kapazität:            {batteryInfo.DesignCapacityDisplay}", fontBody, new XSolidBrush(colorDark), 52, 166);
            gfx.DrawString($"Volle Ladekapazität:         {batteryInfo.FullChargeCapacityDisplay}", fontBody, new XSolidBrush(colorDark), 52, 178);
            gfx.DrawString($"Batterie-Chemie:             {batteryInfo.Chemistry}", fontBody, new XSolidBrush(colorDark), 52, 190);

            // Column 2: System Specs & Test details
            gfx.DrawString("TEST-INFO & RUNTIME", fontSubHeading, new XSolidBrush(colorPrimary), 320, 128);
            gfx.DrawString($"Startzeit:    {startTime:dd.MM.yyyy HH:mm:ss}", fontBody, new XSolidBrush(colorDark), 320, 142);
            gfx.DrawString($"Endzeit:      {endTime:dd.MM.yyyy HH:mm:ss}", fontBody, new XSolidBrush(colorDark), 320, 154);
            
            var testDuration = endTime - startTime;
            gfx.DrawString($"Testdauer:    {testDuration.Hours:D2}:{testDuration.Minutes:D2}:{testDuration.Seconds:D2}", fontBody, new XSolidBrush(colorDark), 320, 166);
            gfx.DrawString($"CPU:          {cpuName}", fontBody, new XSolidBrush(colorDark), 320, 178);
            gfx.DrawString($"Grafikkarte:  {gpuName}", fontBody, new XSolidBrush(colorDark), 320, 190);
            gfx.DrawString($"PC Modell:    {systemInfo.FullSystemName}", fontBody, new XSolidBrush(colorDark), 320, 202);
            gfx.DrawString($"Seriennummer: {systemInfo.SerialNumber}", fontBody, new XSolidBrush(colorDark), 320, 214);
            gfx.DrawString($"Mainboard:    {systemInfo.MotherboardModel}", fontBody, new XSolidBrush(colorDark), 320, 226);

            // --- 3. Section: Zusammenfassung Stresstest ---
            gfx.DrawString("ZUSAMMENFASSUNG DES STRESSTESTS", fontHeading, new XSolidBrush(colorDark), 40, 260);

            // Draw Summary Box
            var rectSummaryBox = new XRect(40, 272, 515, 60);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(240, 253, 250)), rectSummaryBox); // Light mint green bg
            gfx.DrawRectangle(new XPen(XColor.FromArgb(186, 230, 218), 1), rectSummaryBox);

            var totalDrain = startPercent - endPercent;
            double hours = testDuration.TotalHours;
            double dischargeRate = hours > 0 ? totalDrain / hours : 0;
            double estimatedRuntime = dischargeRate > 0 ? endPercent / dischargeRate : 0;

            gfx.DrawString($"Anfangsladung: {startPercent:F0}%", fontBodyBold, new XSolidBrush(colorDark), 52, 278);
            gfx.DrawString($"Endladung:     {endPercent:F0}%", fontBodyBold, new XSolidBrush(colorDark), 52, 292);
            gfx.DrawString($"Gesamt-Verlust: {totalDrain:F0}% in {testDuration.Minutes} Min.", fontBodyBold, new XSolidBrush(XColor.FromArgb(220, 38, 38)), 52, 306);

            gfx.DrawString($"Entladungsrate:         ~{dischargeRate:F1}% / Std.", fontBodyBold, new XSolidBrush(colorDark), 320, 278);
            gfx.DrawString($"Belastungstyp:          YouTube 4K HDR Stress-Test", fontBody, new XSolidBrush(colorDark), 320, 292);
            
            string runtimeStr = dischargeRate > 0 ? $"{estimatedRuntime:F1} Std." : "Unbekannt";
            gfx.DrawString($"Proj. Restlaufzeit:     ~{runtimeStr} (unter Last)", fontBodyBold, new XSolidBrush(colorDark), 320, 306);

            // --- 4. Section: Chronologischer Testverlauf (Timeline Table) ---
            gfx.DrawString("PROTOKOLL DES TESTVERLAUFS", fontHeading, new XSolidBrush(colorDark), 40, 355);

            // Draw Table Header
            double histY = 370;
            double rowHeight = 18;
            var brushHeader = new XSolidBrush(XColor.FromArgb(241, 245, 249));
            gfx.DrawRectangle(brushHeader, 40, histY, 515, rowHeight);
            gfx.DrawRectangle(new XPen(colorBorder, 1), 40, histY, 515, rowHeight);

            gfx.DrawString("Eintrag", fontBodyBold, new XSolidBrush(colorDark), 50, histY + 12);

            // Downsample log list to fit up to 20 entries on the page
            var displayList = new System.Collections.Generic.List<string>();
            if (logEntries.Count <= 20)
            {
                displayList.AddRange(logEntries);
            }
            else
            {
                displayList.Add(logEntries[0]); // Start
                
                double step = (double)(logEntries.Count - 1) / 19;
                for (int idx = 1; idx < 19; idx++)
                {
                    int targetIdx = (int)Math.Round(idx * step);
                    if (targetIdx > 0 && targetIdx < logEntries.Count - 1)
                    {
                        displayList.Add(logEntries[targetIdx]);
                    }
                }
                
                displayList.Add(logEntries[logEntries.Count - 1]); // End
            }

            // Draw Rows of History
            double currentY = histY + rowHeight;
            bool isAltRow = false;
            var brushAltRow = new XSolidBrush(XColor.FromArgb(250, 251, 252));
            var brushWhite = new XSolidBrush(XColor.FromArgb(255, 255, 255));

            foreach (var logLine in displayList)
            {
                // Alternating background
                gfx.DrawRectangle(isAltRow ? brushAltRow : brushWhite, 40, currentY, 515, rowHeight);
                gfx.DrawRectangle(new XPen(colorBorder, 1), 40, currentY, 515, rowHeight);

                gfx.DrawString(logLine, fontBody, new XSolidBrush(colorDark), 50, currentY + 12);

                currentY += rowHeight;
                isAltRow = !isAltRow;
            }

            // --- 5. Footer ---
            gfx.DrawLine(new XPen(colorBorder, 1), 40, 800, 555, 800);
            gfx.DrawString("Generiert mit CPM Hardware-Diagnose Tool", fontMuted, new XSolidBrush(colorGray), 40, 814);
            gfx.DrawString("Seite 1 von 1", fontMuted, new XSolidBrush(colorGray), 510, 814);

            // Save PDF
            document.Save(filePath);
        }
    }
}
