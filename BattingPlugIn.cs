using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.DocObjects.Custom;
using Rhino.PlugIns;
using Rhino.UI;

[assembly: Guid("3F7A2C91-5B4E-4D0A-9E61-7C2B8D4F1A35")]
[assembly: PlugInDescription(DescriptionType.Organization, "pbrgle1")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/pbrgle1/Batting")]

namespace Batting
{
    /// <summary>
    /// Plugin entry point. Loads at startup so batting objects show their base
    /// grips as soon as they are selected.
    /// </summary>
    public class BattingPlugIn : PlugIn
    {
        public BattingPlugIn()
        {
            Instance = this;
        }

        public static BattingPlugIn? Instance { get; private set; }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        private BattingDisplay? m_display;

        // Commands running right now. Deselections inside a command (PointsOn
        // deselects what it shows points on, for one) leave the grips alone.
        private int m_commands;
        private bool m_hideQueued;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            CustomObjectGrips.RegisterGripsEnabler(BattingGrips.TurnOn, typeof(BattingGrips));
            RhinoDoc.SelectObjects += OnSelectObjects;
            RhinoDoc.DeselectObjects += (sender, e) => OnDeselect(e.Document);
            RhinoDoc.DeselectAllObjects += (sender, e) => OnDeselect(e.Document);
            Command.BeginCommand += (sender, e) => m_commands++;
            Command.EndCommand += OnEndCommand;
            m_display = new BattingDisplay { Enabled = true };
            return LoadReturnCode.Success;
        }

        protected override void ObjectPropertiesPages(ObjectPropertiesPageCollection collection)
        {
            collection.Add(new BattingPropertiesPage());
        }

        /// <summary>Last batting width used, in the document's units.</summary>
        public double GetWidth(RhinoDoc doc) =>
            Settings.GetDouble("WidthMetres", 0.1) * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);

        public void SetWidth(RhinoDoc doc, double width) =>
            Settings.SetDouble("WidthMetres", width * RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters));

        public bool DeleteInput
        {
            get => Settings.GetBool("DeleteInput", true);
            set => Settings.SetBool("DeleteInput", value);
        }

        /// <summary>The last style used: shape, spacing, corners, alignment and faces.</summary>
        public BattingStyle Style
        {
            get => new BattingStyle
            {
                Shape = Settings.GetDouble("Shape", 1),
                Spacing = Settings.GetDouble("Spacing", 1),
                Corners = BattingData.Valid(Settings.GetInteger("Corners", 0), Corners.Fan),
                Alignment = BattingData.Valid(Settings.GetInteger("Alignment", 0), Alignment.Centre),
                Faces = Settings.GetBool("Faces", false),
            };
            set
            {
                Settings.SetDouble("Shape", value.Shape);
                Settings.SetDouble("Spacing", value.Spacing);
                Settings.SetInteger("Corners", (int)value.Corners);
                Settings.SetInteger("Alignment", (int)value.Alignment);
                Settings.SetBool("Faces", value.Faces);
            }
        }

        // Selecting a batting draws its base; outside a command it also turns
        // on the base grips, ready to edit.
        private void OnSelectObjects(object? sender, RhinoObjectSelectionEventArgs e)
        {
            var doc = e.Document;
            var selected = e.RhinoObjects.Where(BattingSet.IsBatting).Select(obj => obj.Id).ToList();
            if (selected.Count == 0)
                return;
            foreach (var primary in BattingSet.Primaries(e.RhinoObjects.Where(BattingSet.IsBatting)))
                BattingDisplay.Track(primary);
            // Runs once Rhino has finished the selection. A selection made
            // inside a command (Move asking for objects, say) is left alone.
            Eto.Forms.Application.Instance.AsyncInvoke(() =>
            {
                if (Command.InCommand())
                    return;
                var still = selected.Select(doc.Objects.FindId).Where(obj => obj != null && obj.IsSelected(false) > 0);
                foreach (var primary in BattingSet.Primaries(still!))
                {
                    if (!primary.GripsOn)
                        BattingGrips.TurnOn(primary);
                }
                doc.Views.Redraw();
            });
        }

        // Deselecting a batting by clicking elsewhere or in empty space turns
        // its base grips off again, which also hides the base. Not when one of
        // its grips is what got selected: then the base is being edited.
        private void OnDeselect(RhinoDoc doc)
        {
            if (m_commands > 0 || m_hideQueued)
                return;
            m_hideQueued = true;
            Eto.Forms.Application.Instance.AsyncInvoke(() =>
            {
                m_hideQueued = false;
                if (m_commands > 0)
                    return;
                foreach (var obj in BattingDisplay.Shown(doc))
                {
                    if (obj.GripsOn && obj.IsSelected(false) == 0 && !obj.GripsSelected &&
                        BattingSet.Pieces(obj).All(piece => piece.IsSelected(false) == 0))
                        obj.GripsOn = false;
                }
                doc.Views.Redraw();
            });
        }

        // PointsOn (F10, or a "! _PointsOn" macro) gives a batting the control
        // points of its pattern; swap them for the base grips.
        private void OnEndCommand(object? sender, CommandEventArgs e)
        {
            m_commands = Math.Max(0, m_commands - 1);
            if (e.CommandEnglishName != "PointsOn" || e.Document == null)
                return;
            var doc = e.Document;
            Eto.Forms.Application.Instance.AsyncInvoke(() =>
            {
                foreach (var obj in doc.Objects.ToList())
                {
                    if (!obj.GripsOn)
                        continue;
                    if (BattingData.From(obj.Geometry) != null)
                    {
                        obj.GripsOn = false;
                        BattingGrips.TurnOn(obj);
                    }
                    else if (BattingPiece.From(obj.Geometry) != null)
                    {
                        // the grips live on the piece that carries the data
                        obj.GripsOn = false;
                    }
                }
                doc.Views.Redraw();
            });
        }
    }
}
