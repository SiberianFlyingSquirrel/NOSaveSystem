using System.Diagnostics;
using System.Text;
namespace NOSaveSystem
{
    /// <summary>v0.9.9: per-phase load timing. One 'LOADTIME' summary line per F6 load.</summary>
    internal static class NoSaveLoadTime
    {
        private static Stopwatch _sw; private static long _last; private static StringBuilder _sb;
        internal static void Begin() { _sw = Stopwatch.StartNew(); _last = 0; _sb = new StringBuilder(); }
        internal static void Lap(string phase)
        {
            if (_sw == null) Begin();
            long now = _sw.ElapsedMilliseconds;
            _sb.Append(' ').Append(phase).Append('=').Append(now - _last);
            _last = now;
        }
        internal static void Finish(StringBuilder plan)
        {
            if (_sw == null) return;
            string line = "LOADTIME total=" + _sw.ElapsedMilliseconds + "ms" + _sb;
            NOSaveSystemPlugin.Log?.LogInfo(line);
            plan?.AppendLine("  " + line);
            _sw = null;
        }
    }
}
