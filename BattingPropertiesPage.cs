using System.Globalization;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;

namespace Batting
{
    /// <summary>
    /// "Batting" page in the Properties panel: the width of the selected
    /// battings, typed in or measured between two points, and their exclusions.
    /// </summary>
    public class BattingPropertiesPage : ObjectPropertiesPage
    {
        private BattingPanel? m_panel;

        public BattingPropertiesPage()
        {
            Instance = this;
        }

        public static BattingPropertiesPage? Instance { get; private set; }

        public override string EnglishPageTitle => "Batting";

        public override ObjectType SupportedTypes => ObjectType.Curve;

        public override object PageControl => m_panel ??= new BattingPanel(this);

        public override System.Drawing.Icon PageIcon(System.Drawing.Size sizeInPixels) =>
            BattingIcon.Get(sizeInPixels);

        public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) =>
            e.Objects.Any(BattingSet.IsBatting);

        public override void UpdatePage(ObjectPropertiesPageEventArgs e)
        {
            var data = BattingSet.Primaries(e.Objects)
                .Select(obj => BattingData.From(obj.Geometry)!)
                .ToList();
            if (data.Count > 0 && e.Document != null)
                m_panel?.Show(data, e.Document);
        }

        /// <summary>Rebuilds the selected battings at the new width, as one undo step.</summary>
        internal void SetWidth(double width)
        {
            ModifyPage(e =>
            {
                foreach (var batting in BattingSet.Primaries(e.Objects))
                    BattingSet.Replace(batting, BattingData.From(batting.Geometry)!.Rebuild(width: width));
                BattingPlugIn.Instance?.SetWidth(e.Document, width);
                e.Document.Views.Redraw();
            });
        }

        /// <summary>Rebuilds the selected battings with a new shape, as one undo step.</summary>
        internal void SetShape(double shape)
        {
            ModifyPage(e =>
            {
                foreach (var batting in BattingSet.Primaries(e.Objects))
                    BattingSet.Replace(batting, BattingData.From(batting.Geometry)!.Rebuild(shape: shape));
                if (BattingPlugIn.Instance != null)
                    BattingPlugIn.Instance.Shape = shape;
                e.Document.Views.Redraw();
            });
        }

        /// <summary>Rebuilds the selected battings with a new corner type, as one undo step.</summary>
        internal void SetCorners(Corners corners)
        {
            ModifyPage(e =>
            {
                foreach (var batting in BattingSet.Primaries(e.Objects))
                    BattingSet.Replace(batting, BattingData.From(batting.Geometry)!.Rebuild(corners: corners));
                if (BattingPlugIn.Instance != null)
                    BattingPlugIn.Instance.Corners = corners;
                e.Document.Views.Redraw();
            });
        }

        /// <summary>Removes all exclusions from the selected battings, as one undo step.</summary>
        internal void ClearExclusions()
        {
            ModifyPage(e =>
            {
                foreach (var batting in BattingSet.Primaries(e.Objects))
                    BattingSet.Replace(batting, BattingData.From(batting.Geometry)!.Rebuild(exclusions: new List<Curve>()));
                e.Document.Views.Redraw();
            });
        }
    }

    internal class BattingPanel : Panel
    {
        private readonly BattingPropertiesPage m_page;
        private readonly TextBox m_width = new TextBox();
        private readonly Label m_units = new Label { VerticalAlignment = VerticalAlignment.Center };
        private readonly Label m_exclusions = new Label { VerticalAlignment = VerticalAlignment.Center };
        private readonly Slider m_shape = new Slider { MinValue = 0, MaxValue = 100 };
        private readonly Label m_shapeValue = new Label { Width = 40, VerticalAlignment = VerticalAlignment.Center };
        private readonly UITimer m_shapeTimer = new UITimer { Interval = 0.3 };
        private readonly DropDown m_corners = new DropDown();
        private string m_shown = "";
        private bool m_showing;

        public BattingPanel(BattingPropertiesPage page)
        {
            m_page = page;

            foreach (var name in Enum.GetNames(typeof(Corners)))
                m_corners.Items.Add(name);
            m_corners.SelectedIndexChanged += (sender, e) =>
            {
                if (!m_showing && m_corners.SelectedIndex >= 0)
                    m_page.SetCorners((Corners)m_corners.SelectedIndex);
            };

            // Applied once the slider has stopped for a moment, so dragging it
            // rebuilds the battings once and makes one undo step.
            m_shape.ValueChanged += (sender, e) =>
            {
                m_shapeValue.Text = FormatShape(m_shape.Value / 100.0);
                if (m_showing)
                    return;
                m_shapeTimer.Stop();
                m_shapeTimer.Start();
            };
            m_shapeTimer.Elapsed += (sender, e) =>
            {
                m_shapeTimer.Stop();
                m_page.SetShape(m_shape.Value / 100.0);
            };

            m_width.KeyDown += (sender, e) =>
            {
                if (e.Key == Keys.Enter)
                {
                    Apply();
                    e.Handled = true;
                }
            };
            m_width.LostFocus += (sender, e) => Apply();

            // the commands work on the selection, which is what this page shows
            var pick = new Button { Text = "Pick", Width = 50, ToolTip = "Measure the width between two points" };
            pick.Click += (sender, e) => RhinoApp.RunScript("_BattingWidth", false);
            var add = new Button { Text = "Add", Width = 50, ToolTip = "Pick closed curves to cut out of the batting" };
            add.Click += (sender, e) => RhinoApp.RunScript("_BattingExclude", false);
            var clear = new Button { Text = "Clear", Width = 50, ToolTip = "Remove all exclusions" };
            clear.Click += (sender, e) => m_page.ClearExclusions();

            Content = new StackLayout
            {
                Padding = 6,
                Spacing = 6,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    Row("Width", m_width, m_units, pick),
                    Row("Shape", m_shape, m_shapeValue),
                    Row("Corners", m_corners),
                    Row("Exclusions", m_exclusions, add, clear),
                },
            };
        }

        // One line of the page: a label, a field that takes the spare width,
        // then units or buttons. The labels share a width so the fields line up.
        private static TableLayout Row(string label, Control field, params Control[] after)
        {
            var cells = new List<TableCell>
            {
                new Label { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center },
                new TableCell(field, true),
            };
            cells.AddRange(after.Select(control => new TableCell(control)));
            return new TableLayout
            {
                Spacing = new Eto.Drawing.Size(6, 0),
                Rows = { new TableRow(cells) },
            };
        }

        public void Show(List<BattingData> battings, RhinoDoc doc)
        {
            double first = battings[0].Width;
            bool same = battings.All(data => Math.Abs(data.Width - first) <= doc.ModelAbsoluteTolerance);
            m_shown = same ? BattingInput.Format(first) : "varies";
            m_width.Text = m_shown;
            m_units.Text = doc.GetUnitSystemName(true, false, false, true);

            int count = battings[0].Exclusions.Count;
            m_exclusions.Text = battings.Any(data => data.Exclusions.Count != count) ? "varies"
                : count == 0 ? "none" : count.ToString(CultureInfo.InvariantCulture);

            double shape = battings[0].Shape;
            var corners = battings[0].Corners;
            m_showing = true;
            m_shapeTimer.Stop();
            m_shape.Value = (int)Math.Round(100 * shape);
            m_shapeValue.Text = battings.Any(data => Math.Abs(data.Shape - shape) > 0.005) ? "varies" : FormatShape(shape);
            // blank when the battings differ
            m_corners.SelectedIndex = battings.All(data => data.Corners == corners) ? (int)corners : -1;
            m_showing = false;
        }

        // 0 zig-zag ... 1 curved
        private static string FormatShape(double shape) => shape.ToString("0.##", CultureInfo.InvariantCulture);

        private void Apply()
        {
            string text = m_width.Text.Trim();
            if (text == m_shown)
                return;
            // accept a decimal comma as well as a point
            if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double width) &&
                width > 0)
            {
                m_shown = text;
                m_page.SetWidth(width);
            }
            else
            {
                m_width.Text = m_shown;
            }
        }
    }

    /// <summary>A few loops of the real pattern, drawn for the page tab.</summary>
    internal static class BattingIcon
    {
        private static readonly Dictionary<int, System.Drawing.Icon> s_icons = new Dictionary<int, System.Drawing.Icon>();

        public static System.Drawing.Icon Get(System.Drawing.Size size)
        {
            int px = Math.Max(16, Math.Min(size.Width, size.Height));
            if (s_icons.TryGetValue(px, out var icon))
                return icon;

            const double length = 1.2;
            var curve = BattingPattern.Create(new LineCurve(Point3d.Origin, new Point3d(length, 0, 0)), 1.0, Vector3d.ZAxis)[0];
            double margin = px / 12.0;
            double scale = (px - 2 * margin) / length;
            var points = curve.DivideByCount(240, true)
                .Select(t => curve.PointAt(t))
                .Select(p => new System.Drawing.PointF((float)(margin + p.X * scale), (float)(0.5 * px - p.Y * scale)))
                .ToArray();

            int grey = Rhino.Runtime.HostUtils.RunningInDarkMode ? 225 : 50;
            using var bitmap = new System.Drawing.Bitmap(px, px);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(grey, grey, grey), Math.Max(1.5f, px / 12f)))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.DrawLines(pen, points);
            }
            icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
            s_icons[px] = icon;
            return icon;
        }
    }
}
