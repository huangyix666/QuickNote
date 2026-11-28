using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class UiStyle
{
    internal enum ToolbarIcon { New, Lock, Settings, Previous, Next, Pin }

    internal static readonly Icon AppIcon = LoadIcon();
    private static readonly PointF[] GearOutline = CreateGearOutline();

    private static Icon LoadIcon()
    {
        using (var stream = typeof(UiStyle).Assembly.GetManifestResourceStream("QuickNote.AppIcon.ico"))
            return new Icon(stream);
    }

    private static bool Animate
    {
        get { return SystemInformation.IsMenuAnimationEnabled && !SystemInformation.HighContrast; }
    }

    internal static Button ActionButton(string text, bool primary = false)
    {
        return new RoundedButton(primary) {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(9, 2, 9, 2)
        };
    }

    internal static Button IconButton(ToolbarIcon icon, string name, bool primary = false, int size = 30)
    {
        return new RoundedButton(primary, icon) {
            AccessibleName = name, Size = new Size(size, size), Margin = new Padding(3, 1, 3, 1)
        };
    }

    internal static void SetToggleState(Button button, bool enabled)
    {
        button.Text = enabled ? "已开启" : "未开启";
        var color = enabled ? Color.FromArgb(36, 107, 73) : Color.FromArgb(159, 52, 53);
        ((RoundedButton)button).SetPalette(color, Color.White);
    }

    internal static void SetIconActive(Button button, bool active)
    {
        ((RoundedButton)button).SetPrimaryPalette(active);
    }

    internal static void FadeIn(Form form)
    {
        if (!Animate) return;
        form.Opacity = .84;
        var timer = new Timer { Interval = 15 };
        timer.Tick += delegate {
            double opacity = Math.Min(1, form.Opacity + .025);
            form.Opacity = opacity;
            if (opacity >= 1) timer.Stop();
        };
        form.Shown += delegate { timer.Start(); };
        form.Disposed += delegate { timer.Dispose(); };
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        return Color.FromArgb(
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static PointF[] CreateGearOutline()
    {
        const int teeth = 6;
        const double toothAngle = 2 * Math.PI / teeth;
        var points = new PointF[teeth * 4];
        for (int tooth = 0; tooth < teeth; tooth++)
        {
            double center = tooth * toothAngle - Math.PI / 2;
            int index = tooth * 4;
            points[index] = GearPoint(center - .26 * toothAngle, 5.3f);
            points[index + 1] = GearPoint(center - .15 * toothAngle, 7.2f);
            points[index + 2] = GearPoint(center + .15 * toothAngle, 7.2f);
            points[index + 3] = GearPoint(center + .26 * toothAngle, 5.3f);
        }
        return points;
    }

    private static PointF GearPoint(double angle, float radius)
    {
        return new PointF((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius);
    }

    private sealed class RoundedButton : Button
    {
        private const int FadeSteps = 6;
        private readonly Timer timer = new Timer { Interval = 15 };
        private Color normal, hover, pressed;
        private readonly ToolbarIcon? icon;
        private Color from, to;
        private Size regionSize;
        private int regionDiameter, step;
        private bool pointerInside, mouseDown;

        internal RoundedButton(bool primary, ToolbarIcon? icon = null)
        {
            this.icon = icon;
            Anchor = AnchorStyles.None;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetPrimaryPalette(primary);
            timer.Tick += delegate {
                ApplyColor(Blend(from, to, ++step / (float)FadeSteps));
                if (step == FadeSteps) timer.Stop();
            };
        }

        internal void SetPrimaryPalette(bool primary)
        {
            SetPalette(primary ? Color.FromArgb(38, 87, 71) : Color.FromArgb(236, 241, 235),
                       primary ? Color.White : Color.FromArgb(29, 57, 48));
        }

        internal void SetPalette(Color background, Color foreground)
        {
            bool dark = foreground == Color.White;
            normal = background;
            hover = Blend(normal, dark ? Color.White : Color.Black, dark ? .12f : .06f);
            pressed = Blend(normal, Color.Black, .16f);
            ForeColor = foreground;
            timer.Stop();
            to = !pointerInside ? normal : mouseDown ? pressed : hover;
            ApplyColor(to);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int diameter = Math.Min(Math.Min(ClientSize.Width, ClientSize.Height),
                                    (int)Math.Round(14 * e.Graphics.DpiX / 96));
            if (diameter > 1 && (regionSize != ClientSize || regionDiameter != diameter))
            {
                regionSize = ClientSize;
                regionDiameter = diameter;
                using (var shape = new GraphicsPath())
                {
                    int right = ClientSize.Width - diameter;
                    int bottom = ClientSize.Height - diameter;
                    shape.AddArc(0, 0, diameter, diameter, 180, 90);
                    shape.AddArc(right, 0, diameter, diameter, 270, 90);
                    shape.AddArc(right, bottom, diameter, diameter, 0, 90);
                    shape.AddArc(0, bottom, diameter, diameter, 90, 90);
                    shape.CloseFigure();
                    Region = new Region(shape);
                }
            }
            base.OnPaint(e);
            if (icon.HasValue) PaintIcon(e.Graphics, icon.Value);
        }

        private void PaintIcon(Graphics graphics, ToolbarIcon glyph)
        {
            float unit = Math.Min(graphics.DpiX / 96, Math.Min(ClientSize.Width, ClientSize.Height) / 24f);
            if (unit <= 0) return;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TranslateTransform(ClientSize.Width / 2f, ClientSize.Height / 2f);
                graphics.ScaleTransform(unit, unit);
                using (var pen = new Pen(Enabled ? ForeColor : SystemColors.GrayText, 1.7f))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    switch (glyph)
                    {
                        case ToolbarIcon.New:
                            graphics.DrawLine(pen, -5.5f, 0, 5.5f, 0);
                            graphics.DrawLine(pen, 0, -5.5f, 0, 5.5f);
                            break;
                        case ToolbarIcon.Lock:
                            using (var shackle = new GraphicsPath())
                            {
                                shackle.AddLine(-3, -1, -3, -4);
                                shackle.AddArc(-3, -7, 6, 6, 180, 180);
                                shackle.AddLine(3, -4, 3, -1);
                                graphics.DrawPath(pen, shackle);
                            }
                            graphics.DrawRectangle(pen, -5.5f, -1, 11, 7);
                            graphics.DrawLine(pen, 0, 2, 0, 3.5f);
                            break;
                        case ToolbarIcon.Settings:
                            graphics.DrawPolygon(pen, GearOutline);
                            graphics.DrawEllipse(pen, -2.1f, -2.1f, 4.2f, 4.2f);
                            break;
                        case ToolbarIcon.Pin:
                            graphics.RotateTransform(35);
                            graphics.DrawPolygon(pen, new[] {
                                new PointF(-4, -6), new PointF(4, -6),
                                new PointF(2.5f, -4), new PointF(2.5f, -1),
                                new PointF(5, 2), new PointF(-5, 2),
                                new PointF(-2.5f, -1), new PointF(-2.5f, -4)
                            });
                            graphics.DrawLine(pen, 0, 2, 0, 7);
                            break;
                        case ToolbarIcon.Previous:
                        case ToolbarIcon.Next:
                            float direction = glyph == ToolbarIcon.Previous ? 1 : -1;
                            graphics.DrawLines(pen, new[] {
                                new PointF(direction * 2.5f, -4.2f),
                                new PointF(direction * -2.1f, 0),
                                new PointF(direction * 2.5f, 4.2f)
                            });
                            break;
                    }
                }
            }
            finally { graphics.Restore(state); }
        }

        private void ApplyColor(Color color)
        {
            BackColor = color;
            FlatAppearance.MouseOverBackColor = color;
            FlatAppearance.MouseDownBackColor = color;
        }

        private void UpdateColor()
        {
            Color target = !pointerInside ? normal : mouseDown ? pressed : hover;
            if (to == target) return;
            from = BackColor;
            to = target;
            step = 0;
            if (Animate) timer.Start();
            else ApplyColor(target);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            pointerInside = true;
            UpdateColor();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            pointerInside = false;
            UpdateColor();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                pointerInside = mouseDown = true;
                UpdateColor();
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && mouseDown)
            {
                mouseDown = false;
                UpdateColor();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
