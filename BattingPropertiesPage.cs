using System.Globalization;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;

namespace Batting
{
    /// <summary>
    /// "Batting" page in the Properties panel: the width and style of the
    /// selected battings, and their exclusions.
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

        // an embedded icon rather than one drawn with System.Drawing, which
        // only exists on Windows
        public override string PageIconEmbeddedResourceString =>
            Rhino.Runtime.HostUtils.RunningInDarkMode ? "Batting.page-dark.ico" : "Batting.page.ico";

        public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) =>
            e.Objects.Any(BattingSet.IsBatting);

        public override void UpdatePage(ObjectPropertiesPageEventArgs e)
        {
            var battings = BattingSet.Primaries(e.Objects);
            var data = battings.Select(obj => BattingData.From(obj.Geometry)!).ToList();
            if (data.Count > 0 && e.Document != null)
                m_panel?.Show(data, e.Document);
        }

        /// <summary>Rebuilds the selected battings at the new width, as one undo step.</summary>
        internal void SetWidth(double width)
        {
            ModifyPage(e =>
            {
                foreach (var batting in BattingSet.Primaries(e.Objects))
                {
                    var data = BattingData.From(batting.Geometry)!;
                    if (!data.IsBetween)
                        BattingSet.Replace(batting, data.Rebuild(width: width));
                }
                BattingPlugIn.Instance?.SetWidth(e.Document, width);
                e.Document.Views.Redraw();
            });
        }

        /// <summary>
        /// Rebuilds the selected battings with one style setting changed, as
        /// one undo step, and remembers it for new battings.
        /// </summary>
        internal void SetStyle(Func<BattingStyle, BattingStyle> change)
        {
            ModifyPage(e =>
            {
                foreach (var batting in BattingSet.Primaries(e.Objects))
                {
                    var data = BattingData.From(batting.Geometry)!;
                    BattingSet.Replace(batting, data.Rebuild(style: change(data.Style)));
                }
                if (BattingPlugIn.Instance is BattingPlugIn plugIn)
                    plugIn.Style = change(plugIn.Style);
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
        private readonly Button m_pick = new Button { Text = "Pick", Width = 50, ToolTip = "Measure the width between two points" };
        private readonly Label m_exclusions = new Label { VerticalAlignment = VerticalAlignment.Center };
        private readonly DebouncedSlider m_shape;
        private readonly DebouncedSlider m_spacing;
        private readonly DropDown m_corners = new DropDown();
        private readonly DropDown m_alignment = new DropDown();
        private readonly CheckBox m_faces = new CheckBox { Text = "Draw the faces" };
        private string m_shown = "";
        private bool m_showing;

        public BattingPanel(BattingPropertiesPage page)
        {
            m_page = page;

            m_shape = new DebouncedSlider(0, 1, value => m_page.SetStyle(style => style with { Shape = value }));
            m_spacing = new DebouncedSlider(BattingStyle.MinSpacing, BattingStyle.MaxSpacing,
                                            value => m_page.SetStyle(style => style with { Spacing = value }));

            foreach (var name in Enum.GetNames(typeof(Corners)))
                m_corners.Items.Add(name);
            m_corners.SelectedIndexChanged += (sender, e) =>
            {
                if (!m_showing && m_corners.SelectedIndex >= 0)
                    m_page.SetStyle(style => style with { Corners = (Corners)m_corners.SelectedIndex });
            };
            m_alignment.SelectedIndexChanged += (sender, e) =>
            {
                if (!m_showing && m_alignment.SelectedIndex >= 0)
                    m_page.SetStyle(style => style with { Alignment = (Alignment)m_alignment.SelectedIndex });
            };
            m_faces.CheckedChanged += (sender, e) =>
            {
                if (!m_showing && m_faces.Checked is bool faces)
                    m_page.SetStyle(style => style with { Faces = faces });
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
            m_pick.Click += (sender, e) => RhinoApp.RunScript("_BattingWidth", false);
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
                    Row("Width", m_width, m_units, m_pick),
                    Row("Shape", m_shape.Slider, m_shape.Value),
                    Row("Spacing", m_spacing.Slider, m_spacing.Value),
                    Row("Corners", m_corners),
                    Row("Align", m_alignment),
                    Row("Faces", m_faces),
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
            m_showing = true;

            // battings between two curves take their width from the curves,
            // and their corners and alignment don't apply
            var sized = battings.Where(data => !data.IsBetween).ToList();
            m_width.Enabled = m_pick.Enabled = m_corners.Enabled = m_alignment.Enabled = sized.Count > 0;
            var widths = sized.Count > 0 ? sized : battings;
            double first = widths[0].Width;
            bool same = widths.All(data => Math.Abs(data.Width - first) <= doc.ModelAbsoluteTolerance);
            m_shown = same ? BattingInput.Format(first) : "varies";
            m_width.Text = m_shown;
            m_units.Text = doc.GetUnitSystemName(true, false, false, true);

            int count = battings[0].Exclusions.Count;
            m_exclusions.Text = battings.Any(data => data.Exclusions.Count != count) ? "varies"
                : count == 0 ? "none" : count.ToString(CultureInfo.InvariantCulture);

            var styles = battings.Select(data => data.Style).ToList();
            m_shape.Show(styles.Select(style => style.Shape));
            m_spacing.Show(styles.Select(style => style.Spacing));
            // blank when the battings differ
            m_corners.SelectedIndex = Same(styles.Select(style => (int)style.Corners)) ?? -1;
            bool closed = sized.Count > 0 && sized.All(data => data.Base!.IsClosed);
            m_alignment.Items.Clear();
            foreach (var name in BattingStyle.AlignmentNames(closed))
                m_alignment.Items.Add(name);
            m_alignment.SelectedIndex = Same(styles.Select(style => (int)style.Alignment)) ?? -1;
            // a third state only to show that the battings differ
            bool? faces = Same(styles.Select(style => style.Faces ? 1 : 0)) is int drawn ? drawn == 1 : null;
            m_faces.ThreeState = faces == null;
            m_faces.Checked = faces;

            m_showing = false;
        }

        private static int? Same(IEnumerable<int> values)
        {
            var distinct = values.Distinct().ToList();
            return distinct.Count == 1 ? distinct[0] : null;
        }

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

        /// <summary>
        /// A slider and its value, applied once the slider has stopped for a
        /// moment, so dragging it rebuilds the battings once and makes one
        /// undo step.
        /// </summary>
        private class DebouncedSlider
        {
            private const int Steps = 100;
            private readonly double m_min;
            private readonly double m_max;
            private readonly UITimer m_timer = new UITimer { Interval = 0.3 };
            private bool m_showing;

            public DebouncedSlider(double min, double max, Action<double> apply)
            {
                m_min = min;
                m_max = max;
                Slider.ValueChanged += (sender, e) =>
                {
                    Value.Text = Format(Current);
                    if (m_showing)
                        return;
                    m_timer.Stop();
                    m_timer.Start();
                };
                m_timer.Elapsed += (sender, e) =>
                {
                    m_timer.Stop();
                    apply(Current);
                };
            }

            public Slider Slider { get; } = new Slider { MinValue = 0, MaxValue = Steps };
            public Label Value { get; } = new Label { Width = 40, VerticalAlignment = VerticalAlignment.Center };

            private double Current => m_min + (m_max - m_min) * Slider.Value / Steps;

            public void Show(IEnumerable<double> values)
            {
                var list = values.ToList();
                m_showing = true;
                m_timer.Stop();
                Slider.Value = (int)Math.Round(Steps * (list[0] - m_min) / (m_max - m_min));
                Value.Text = list.Any(value => Math.Abs(value - list[0]) > 0.005) ? "varies" : Format(list[0]);
                m_showing = false;
            }

            private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
