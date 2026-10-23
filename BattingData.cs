using System.Runtime.InteropServices;
using Rhino.DocObjects.Custom;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Batting
{
    /// <summary>
    /// What makes a curve a batting: the hidden base curve it follows, its
    /// width and style, the normal of the plane it is drawn in, and the closed
    /// curves it is cut away inside. A batting between two faces keeps the
    /// second face too, and its width is then their average distance. The
    /// data lives on the batting curve's geometry, so it moves, copies, undoes
    /// and saves along with it. Without the plug-in a batting is just an
    /// ordinary curve.
    /// </summary>
    [Guid("8B0C5E2A-6C1D-4F57-9C33-2E6F4B7A91D4")]
    public class BattingData : UserData
    {
        private List<Curve> m_exclusions = new List<Curve>();

        public Curve? Base { get; private set; }

        /// <summary>The second face, for a batting between two curves; null otherwise.</summary>
        public Curve? Other { get; private set; }

        public double Width { get; private set; }
        public Vector3d Axis { get; private set; }
        public BattingStyle Style { get; private set; } = new BattingStyle();

        /// <summary>Shared with the pieces exclusions cut the batting into.</summary>
        public Guid SetId { get; private set; }

        /// <summary>Closed curves the batting is cut away inside.</summary>
        public IReadOnlyList<Curve> Exclusions => m_exclusions;

        public bool IsBetween => Other != null;

        public override string Description => "Batting";

        public override bool ShouldWrite => Base != null;

        public static BattingData? From(GeometryBase? geometry) =>
            geometry?.UserData.Find(typeof(BattingData)) as BattingData;

        /// <summary>
        /// The batting for baseCurve, or between baseCurve and other: the
        /// pattern, then the two faces if the style draws them. The first
        /// curve carries the BattingData, the others a BattingPiece. A base
        /// too short to fit a loop on each face, or cut away completely, shows
        /// as the plain base until it is edited. A batting between two curves
        /// works its width out itself. A preview skips turning the pattern
        /// into lines and arcs, which is slow on long curved walls.
        /// </summary>
        public static List<Curve> Build(Curve baseCurve, Curve? other, double width, Vector3d axis, BattingStyle style,
                                        IEnumerable<Curve>? exclusions = null, Guid setId = default, bool preview = false)
        {
            var data = new BattingData
            {
                Base = baseCurve.DuplicateCurve(),
                Other = other?.DuplicateCurve(),
                Width = other == null ? width : BattingPattern.Thickness(baseCurve, other, axis),
                Axis = axis,
                Style = style,
                SetId = setId == Guid.Empty ? Guid.NewGuid() : setId,
                m_exclusions = exclusions?.Select(curve => curve.DuplicateCurve()).ToList() ?? new List<Curve>(),
            };

            var curves = data.Curves(!preview).ToList();
            if (curves.Count == 0)
                curves.Add(baseCurve.DuplicateCurve());

            curves[0].UserData.Add(data);
            for (int i = 1; i < curves.Count; i++)
                curves[i].UserData.Add(new BattingPiece { SetId = data.SetId });
            return curves;
        }

        /// <summary>The pattern and, if drawn, the faces, cut away inside the exclusions.</summary>
        private IEnumerable<Curve> Curves(bool clean)
        {
            var pattern = Other == null
                ? BattingPattern.Create(Base!, Width, Axis, Style, clean)
                : BattingPattern.CreateBetween(Base!, Other, Axis, Style, clean);
            var faces = !Style.Faces ? new List<Curve>()
                : Other == null ? BattingPattern.Faces(Base!, Width, Axis, Style.Alignment)
                : new List<Curve> { Base!.DuplicateCurve(), Other.DuplicateCurve() };
            return pattern.Concat(faces).SelectMany(curve => BattingPattern.CutOut(curve, m_exclusions, Axis, Width));
        }

        /// <summary>This batting built again with some of its settings changed.</summary>
        public List<Curve> Rebuild(Curve? baseCurve = null, Curve? other = null, double? width = null,
                                   BattingStyle? style = null, IEnumerable<Curve>? exclusions = null) =>
            Build(baseCurve ?? Base!, other ?? Other, width ?? Width, Axis, style ?? Style, exclusions ?? Exclusions, SetId);

        protected override void OnDuplicate(UserData source)
        {
            if (source is BattingData data)
            {
                Base = data.Base?.DuplicateCurve();
                Other = data.Other?.DuplicateCurve();
                Width = data.Width;
                Axis = data.Axis;
                Style = data.Style;
                SetId = data.SetId;
                m_exclusions = data.m_exclusions.Select(curve => curve.DuplicateCurve()).ToList();
            }
        }

        protected override void OnTransform(Transform transform)
        {
            base.OnTransform(transform);
            Base?.Transform(transform);
            Other?.Transform(transform);
            foreach (var exclusion in m_exclusions)
                exclusion.Transform(transform);
            var axis = Axis;
            axis.Transform(transform);
            axis.Unitize();
            Axis = axis;
            // A uniform scale scales the width with it. Other transforms leave
            // the width alone; the batting is rebuilt the next time it is edited.
            if (transform.SimilarityType != TransformSimilarityType.NotSimilarity)
                Width *= Math.Pow(Math.Abs(transform.Determinant), 1.0 / 3.0);
        }

        protected override bool Write(BinaryArchiveWriter archive)
        {
            archive.Write3dmChunkVersion(1, 4);
            archive.WriteDouble(Width);
            archive.WriteVector3d(Axis);
            archive.WriteGeometry(Base);
            // added in 1.1
            archive.WriteGuid(SetId);
            archive.WriteInt(m_exclusions.Count);
            foreach (var exclusion in m_exclusions)
                archive.WriteGeometry(exclusion);
            // added in 1.2
            archive.WriteDouble(Style.Shape);
            // added in 1.3
            archive.WriteInt((int)Style.Corners);
            // added in 1.4
            archive.WriteDouble(Style.Spacing);
            archive.WriteInt((int)Style.Alignment);
            archive.WriteBool(Style.Faces);
            archive.WriteBool(Other != null);
            if (Other != null)
                archive.WriteGeometry(Other);
            return true;
        }

        protected override bool Read(BinaryArchiveReader archive)
        {
            archive.Read3dmChunkVersion(out _, out int minor);
            Width = archive.ReadDouble();
            Axis = archive.ReadVector3d();
            Base = archive.ReadGeometry() as Curve;
            m_exclusions = new List<Curve>();
            if (minor >= 1)
            {
                SetId = archive.ReadGuid();
                int count = archive.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    if (archive.ReadGeometry() is Curve exclusion)
                        m_exclusions.Add(exclusion);
                }
            }
            var style = new BattingStyle();
            if (minor >= 2)
                style = style with { Shape = archive.ReadDouble() };
            // battings from before corner types get fans when next rebuilt
            if (minor >= 3)
                style = style with { Corners = Valid(archive.ReadInt(), Corners.Fan) };
            Other = null;
            if (minor >= 4)
            {
                style = style with
                {
                    Spacing = archive.ReadDouble(),
                    Alignment = Valid(archive.ReadInt(), Alignment.Centre),
                    Faces = archive.ReadBool(),
                };
                if (archive.ReadBool())
                    Other = archive.ReadGeometry() as Curve;
            }
            Style = style;
            if (SetId == Guid.Empty)
                SetId = Guid.NewGuid();
            return Base != null;
        }

        /// <summary>value as an enum member, or fallback if it isn't one, e.g. from a newer version.</summary>
        internal static T Valid<T>(int value, T fallback) where T : struct, Enum =>
            Enum.IsDefined(typeof(T), value) ? (T)(object)value : fallback;
    }

    /// <summary>
    /// Marks one of the further pieces of a batting: a piece exclusions cut
    /// it into, or one of its faces. It belongs to the batting in the same
    /// group whose BattingData has the same SetId.
    /// </summary>
    [Guid("5D21A7C4-0B8E-4F6A-A3C9-7E14B2D86F03")]
    public class BattingPiece : UserData
    {
        public Guid SetId { get; set; }

        public override string Description => "Batting piece";

        public override bool ShouldWrite => true;

        public static BattingPiece? From(GeometryBase? geometry) =>
            geometry?.UserData.Find(typeof(BattingPiece)) as BattingPiece;

        protected override void OnDuplicate(UserData source)
        {
            if (source is BattingPiece piece)
                SetId = piece.SetId;
        }

        protected override bool Write(BinaryArchiveWriter archive)
        {
            archive.Write3dmChunkVersion(1, 0);
            archive.WriteGuid(SetId);
            return true;
        }

        protected override bool Read(BinaryArchiveReader archive)
        {
            archive.Read3dmChunkVersion(out _, out _);
            SetId = archive.ReadGuid();
            return true;
        }
    }
}
