using System.Runtime.InteropServices;
using Rhino.DocObjects.Custom;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Batting
{
    /// <summary>
    /// What makes a curve a batting: the hidden base curve it follows, its
    /// width, the normal of the plane it is drawn in, and the closed curves it
    /// is cut away inside. It lives on the batting curve's geometry, so it
    /// moves, copies, undoes and saves along with it. Without the plug-in a
    /// batting is just an ordinary curve.
    /// </summary>
    [Guid("8B0C5E2A-6C1D-4F57-9C33-2E6F4B7A91D4")]
    public class BattingData : UserData
    {
        private List<Curve> m_exclusions = new List<Curve>();

        public Curve? Base { get; private set; }
        public double Width { get; private set; }
        public Vector3d Axis { get; private set; }

        /// <summary>0 is a zig-zag, 1 half-circle loops with crossing lines.</summary>
        public double Shape { get; private set; } = 1;

        /// <summary>How the batting turns sharp corners of its base.</summary>
        public Corners Corners { get; private set; } = Corners.Fan;

        /// <summary>Shared with the pieces exclusions cut the batting into.</summary>
        public Guid SetId { get; private set; }

        /// <summary>Closed curves the batting is cut away inside.</summary>
        public IReadOnlyList<Curve> Exclusions => m_exclusions;

        public override string Description => "Batting";

        public override bool ShouldWrite => Base != null;

        public static BattingData? From(GeometryBase? geometry) =>
            geometry?.UserData.Find(typeof(BattingData)) as BattingData;

        /// <summary>
        /// The batting for baseCurve: one curve, or several when exclusions cut
        /// it apart. The first carries the BattingData, the others a
        /// BattingPiece. A base too short to fit a loop on each face, or cut
        /// away completely, shows as the plain base until it is edited.
        /// </summary>
        public static List<Curve> Build(Curve baseCurve, double width, Vector3d axis, double shape, Corners corners,
                                        IEnumerable<Curve>? exclusions = null, Guid setId = default)
        {
            var data = new BattingData
            {
                Base = baseCurve.DuplicateCurve(),
                Width = width,
                Axis = axis,
                Shape = shape,
                Corners = corners,
                SetId = setId == Guid.Empty ? Guid.NewGuid() : setId,
                m_exclusions = exclusions?.Select(curve => curve.DuplicateCurve()).ToList() ?? new List<Curve>(),
            };

            var curves = BattingPattern.Create(baseCurve, width, axis, shape, corners)
                .SelectMany(batting => BattingPattern.CutOut(batting, data.m_exclusions, axis, width))
                .ToList();
            if (curves.Count == 0)
                curves.Add(baseCurve.DuplicateCurve());

            curves[0].UserData.Add(data);
            for (int i = 1; i < curves.Count; i++)
                curves[i].UserData.Add(new BattingPiece { SetId = data.SetId });
            return curves;
        }

        /// <summary>This batting built again with some of its settings changed.</summary>
        public List<Curve> Rebuild(Curve? baseCurve = null, double? width = null, double? shape = null,
                                   Corners? corners = null, IEnumerable<Curve>? exclusions = null) =>
            Build(baseCurve ?? Base!, width ?? Width, Axis, shape ?? Shape, corners ?? Corners, exclusions ?? Exclusions, SetId);

        protected override void OnDuplicate(UserData source)
        {
            if (source is BattingData data)
            {
                Base = data.Base?.DuplicateCurve();
                Width = data.Width;
                Axis = data.Axis;
                Shape = data.Shape;
                Corners = data.Corners;
                SetId = data.SetId;
                m_exclusions = data.m_exclusions.Select(curve => curve.DuplicateCurve()).ToList();
            }
        }

        protected override void OnTransform(Transform transform)
        {
            base.OnTransform(transform);
            Base?.Transform(transform);
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
            archive.Write3dmChunkVersion(1, 3);
            archive.WriteDouble(Width);
            archive.WriteVector3d(Axis);
            archive.WriteGeometry(Base);
            // added in 1.1
            archive.WriteGuid(SetId);
            archive.WriteInt(m_exclusions.Count);
            foreach (var exclusion in m_exclusions)
                archive.WriteGeometry(exclusion);
            // added in 1.2
            archive.WriteDouble(Shape);
            // added in 1.3
            archive.WriteInt((int)Corners);
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
            Shape = minor >= 2 ? archive.ReadDouble() : 1;
            // battings from before corner types get fans when next rebuilt
            int corners = minor >= 3 ? archive.ReadInt() : 0;
            Corners = Enum.IsDefined(typeof(Corners), corners) ? (Corners)corners : Corners.Fan;
            if (SetId == Guid.Empty)
                SetId = Guid.NewGuid();
            return Base != null;
        }
    }

    /// <summary>
    /// Marks one of the further pieces exclusions cut a batting into. It
    /// belongs to the batting in the same group whose BattingData has the same
    /// SetId.
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
