using System.IO;

namespace Apocars
{
    /// A mod built by the Blender add-on's "Build": Apocars' runtime renamed into its own plugin, reading only its own
    /// folder. The lib leaves such folders alone (the standalone mod builds them itself).
    internal static class Standalone
    {
        public const string Marker = "apocars.standalone";

        public static bool ClaimedBySomeoneElse(string modDir) =>
            File.Exists(Path.Combine(modDir, Marker)) && Library.OnlyFolder == null;
    }
}
