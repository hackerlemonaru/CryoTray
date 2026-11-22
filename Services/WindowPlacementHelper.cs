using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace CryoTray.Services
{
    public static class WindowPlacementHelper
    {
        public static Rect GetMonitorWorkAreaDips(Window window)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    var screen = WinForms.Screen.FromHandle(handle);
                    return ToDipRect(screen.WorkingArea, scaleX, scaleY);
                }

                if (double.IsFinite(window.Left) && double.IsFinite(window.Top) &&
                    window.Width > 0 && window.Height > 0)
                {
                    return GetWorkAreaForSavedPositionDips(
                        window.Left, window.Top, window.Width, window.Height, scaleX, scaleY);
                }
            }
            catch
            {
            }

            return SystemParameters.WorkArea;
        }

        public static Rect GetWorkAreaForSavedPositionDips(
            double leftDip,
            double topDip,
            double widthDip,
            double heightDip,
            double dpiScaleX,
            double dpiScaleY)
        {
            double scaleX = dpiScaleX > 0 ? dpiScaleX : 1.0;
            double scaleY = dpiScaleY > 0 ? dpiScaleY : 1.0;

            try
            {
                var screens = WinForms.Screen.AllScreens;
                if (screens.Length > 0)
                {
                    var workAreas = new List<Rect>(screens.Length);
                    foreach (var screen in screens)
                    {
                        workAreas.Add(ToDipRect(screen.WorkingArea, scaleX, scaleY));
                    }

                    var bounds = new Rect(leftDip, topDip, Math.Max(1, widthDip), Math.Max(1, heightDip));
                    return FindTargetWorkArea(workAreas, bounds, SystemParameters.WorkArea);
                }
            }
            catch
            {
            }

            return SystemParameters.WorkArea;
        }

        internal static Rect FindTargetWorkArea(
            IReadOnlyList<Rect> workAreas,
            Rect windowBounds,
            Rect fallbackWorkArea)
        {
            if (workAreas == null || workAreas.Count == 0)
            {
                return fallbackWorkArea;
            }

            double bestOverlapArea = 0;
            Rect bestArea = fallbackWorkArea;

            foreach (var area in workAreas)
            {
                double overlapLeft = Math.Max(area.Left, windowBounds.Left);
                double overlapTop = Math.Max(area.Top, windowBounds.Top);
                double overlapRight = Math.Min(area.Right, windowBounds.Right);
                double overlapBottom = Math.Min(area.Bottom, windowBounds.Bottom);
                double overlapW = overlapRight - overlapLeft;
                double overlapH = overlapBottom - overlapTop;

                if (overlapW > 0 && overlapH > 0)
                {
                    double overlapArea = overlapW * overlapH;
                    if (overlapArea > bestOverlapArea)
                    {
                        bestOverlapArea = overlapArea;
                        bestArea = area;
                    }
                }
            }

            return bestOverlapArea > 0 ? bestArea : fallbackWorkArea;
        }

        internal static (double Left, double Top) ClampToWorkArea(
            Rect workArea,
            double left,
            double top,
            double width,
            double height,
            double margin = 8)
        {
            double minLeft = workArea.Left + margin;
            double maxLeft = Math.Max(minLeft, workArea.Right - width - margin);
            double minTop = workArea.Top + margin;
            double maxTop = Math.Max(minTop, workArea.Bottom - height - margin);

            double clampedLeft = double.IsFinite(left) ? Math.Clamp(left, minLeft, maxLeft) : maxLeft;
            double clampedTop = double.IsFinite(top) ? Math.Clamp(top, minTop, maxTop) : maxTop;
            return (clampedLeft, clampedTop);
        }

        internal static (double Left, double Top) ComputeSideBySidePosition(
            Rect workArea,
            Rect anchorBounds,
            double childWidth,
            double childHeight,
            double gap = 6)
        {
            double leftCandidate = anchorBounds.Left - childWidth - gap;
            double rightCandidate = anchorBounds.Right + gap;

            double targetLeft;
            if (leftCandidate >= workArea.Left + gap)
            {
                targetLeft = leftCandidate;
            }
            else if (rightCandidate + childWidth <= workArea.Right - gap)
            {
                targetLeft = rightCandidate;
            }
            else
            {
                double spaceLeft = anchorBounds.Left - workArea.Left;
                double spaceRight = workArea.Right - anchorBounds.Right;
                targetLeft = spaceRight >= spaceLeft ? rightCandidate : leftCandidate;
            }

            double targetTop = anchorBounds.Bottom - childHeight;
            return ClampToWorkArea(workArea, targetLeft, targetTop, childWidth, childHeight, gap);
        }

        private static Rect ToDipRect(System.Drawing.Rectangle pixelRect, double scaleX, double scaleY)
        {
            return new Rect(
                pixelRect.Left / scaleX,
                pixelRect.Top / scaleY,
                pixelRect.Width / scaleX,
                pixelRect.Height / scaleY);
        }
    }
}
