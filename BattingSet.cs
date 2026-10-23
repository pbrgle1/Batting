using Rhino.DocObjects;
using Rhino.Geometry;

namespace Batting
{
    /// <summary>
    /// A batting in the document: the object carrying the BattingData and,
    /// when exclusions cut it apart, the pieces grouped with it. Clicking any
    /// piece selects the group, and every edit rebuilds all of them together.
    /// </summary>
    public static class BattingSet
    {
        /// <summary>The object carrying the BattingData for obj, which may be one of its pieces.</summary>
        public static RhinoObject? Primary(RhinoObject obj)
        {
            var data = BattingData.From(obj.Geometry);
            if (data != null)
                return obj;
            var piece = BattingPiece.From(obj.Geometry);
            if (piece == null)
                return null;
            return GroupMates(obj).FirstOrDefault(mate => BattingData.From(mate.Geometry)?.SetId == piece.SetId);
        }

        /// <summary>The battings among objects, each once.</summary>
        public static List<RhinoObject> Primaries(IEnumerable<RhinoObject> objects)
        {
            var primaries = new List<RhinoObject>();
            foreach (var obj in objects)
            {
                var primary = Primary(obj);
                if (primary != null && primaries.All(p => p.Id != primary.Id))
                    primaries.Add(primary);
            }
            return primaries;
        }

        public static bool IsBatting(RhinoObject obj) =>
            BattingData.From(obj.Geometry) != null || BattingPiece.From(obj.Geometry) != null;

        /// <summary>The other pieces of the batting carried by primary.</summary>
        public static List<RhinoObject> Pieces(RhinoObject primary)
        {
            var setId = BattingData.From(primary.Geometry)?.SetId;
            var pieces = new List<RhinoObject>();
            foreach (var mate in GroupMates(primary))
            {
                if (mate.Id != primary.Id && BattingPiece.From(mate.Geometry)?.SetId == setId &&
                    pieces.All(p => p.Id != mate.Id))
                    pieces.Add(mate);
            }
            return pieces;
        }

        private static IEnumerable<RhinoObject> GroupMates(RhinoObject obj)
        {
            var doc = obj.Document;
            if (doc == null)
                yield break;
            foreach (int group in obj.GetGroupList() ?? Array.Empty<int>())
            {
                foreach (var member in doc.Groups.GroupMembers(group) ?? Array.Empty<RhinoObject>())
                    yield return member;
            }
        }

        /// <summary>Adds a newly built batting to doc, all its pieces included.</summary>
        public static void Add(Rhino.RhinoDoc doc, List<Curve> curves, ObjectAttributes? attributes = null)
        {
            var id = attributes == null ? doc.Objects.AddCurve(curves[0]) : doc.Objects.AddCurve(curves[0], attributes);
            var primary = doc.Objects.FindId(id);
            if (primary != null && curves.Count > 1)
                Replace(primary, curves, replacePrimary: false);
        }

        /// <summary>
        /// Puts a rebuilt batting in place of the old one: the object carrying
        /// the data is replaced, its pieces reused, added or deleted, and a
        /// group made the first time it is cut into several. Grip editing
        /// passes replacePrimary false, because Rhino replaces that one itself.
        /// </summary>
        public static void Replace(RhinoObject primary, List<Curve> curves, bool replacePrimary = true)
        {
            var doc = primary.Document;
            var oldPieces = Pieces(primary);
            if (replacePrimary)
                doc.Objects.Replace(primary.Id, curves[0]);

            if (curves.Count > 1 && oldPieces.Count == 0)
            {
                int group = doc.Groups.Add();
                doc.Groups.AddToGroup(group, primary.Id);
            }

            // new pieces share the batting's layer, colour, linetype and groups
            var attributes = (doc.Objects.FindId(primary.Id) ?? primary).Attributes.Duplicate();
            attributes.ObjectId = Guid.Empty;
            for (int i = 1; i < curves.Count; i++)
            {
                if (i - 1 < oldPieces.Count)
                    doc.Objects.Replace(oldPieces[i - 1].Id, curves[i]);
                else
                    doc.Objects.AddCurve(curves[i], attributes);
            }
            for (int i = curves.Count - 1; i < oldPieces.Count; i++)
                doc.Objects.Delete(oldPieces[i], true);
        }
    }
}
