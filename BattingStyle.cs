namespace Batting
{
    /// <summary>Where a batting lies relative to its base curve.</summary>
    public enum Alignment
    {
        /// <summary>Centred on the base.</summary>
        Centre = 0,

        /// <summary>To the left of the base, as it runs; inside a closed base.</summary>
        Left = 1,

        /// <summary>To the right of the base, as it runs; outside a closed base.</summary>
        Right = 2,
    }

    /// <summary>
    /// How a batting looks, apart from its base and width: the settings the
    /// commands remember and the Properties page edits.
    /// </summary>
    public record BattingStyle
    {
        /// <summary>0 is a zig-zag, 1 half-circle loops with crossing lines.</summary>
        public double Shape { get; init; } = 1;

        /// <summary>Distance between the loops, as a multiple of the standard; 0.5 to 2.</summary>
        public double Spacing { get; init; } = 1;

        public Corners Corners { get; init; } = Corners.Fan;

        public Alignment Alignment { get; init; } = Alignment.Centre;

        /// <summary>Also draw the two faces of the insulation layer.</summary>
        public bool Faces { get; init; }

        public const double MinSpacing = 0.5;
        public const double MaxSpacing = 2;

        /// <summary>The names of the alignments, as Inside and Outside for closed bases.</summary>
        public static string[] AlignmentNames(bool closed) =>
            closed ? new[] { "Centre", "Inside", "Outside" } : Enum.GetNames(typeof(Alignment));
    }
}
