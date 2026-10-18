using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace NOSaveSystem
{
    /// <summary>
    /// v0.9.0: fail-safe reflection lookups for game members we could NOT confirm from source/Cecil notes.
    /// Every lookup tries candidate names in order, caches the hit (or miss) per (type, candidate-set), and
    /// logs once what it found or missed so a PC run documents the real member names.
    /// </summary>
    internal static class NoSaveReflect
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Dictionary<string, MemberInfo> _memberCache = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        internal static StringBuilder Report = new StringBuilder();

        private static void LogOnce(string key, string msg)
        {
            if (!_logged.Add(key)) return;
            NOSaveSystemPlugin.Log?.LogInfo("NOSave reflect: " + msg);
            if (Report.Length < 64 * 1024) Report.AppendLine(msg);
        }

        /// <summary>Find a member (field or property) on type or its bases by candidate names; cached.</summary>
        internal static MemberInfo FindMember(Type t, bool isStatic, params string[] candidates)
        {
            if (t == null || candidates == null || candidates.Length == 0) return null;
            string key = t.FullName + (isStatic ? "|S|" : "|I|") + string.Join(",", candidates);
            MemberInfo cached;
            if (_memberCache.TryGetValue(key, out cached)) return cached;
            MemberInfo hit = null;
            var flags = (isStatic ? Stat : Inst) | BindingFlags.FlattenHierarchy;
            foreach (var name in candidates)
            {
                for (var cur = t; cur != null && hit == null; cur = cur.BaseType)
                {
                    try
                    {
                        var p = cur.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                        if (p != null && p.GetIndexParameters().Length == 0) { hit = p; break; }
                        var f = cur.GetField(name, flags | BindingFlags.DeclaredOnly);
                        if (f != null) { hit = f; break; }
                    }
                    catch (AmbiguousMatchException) { }
                    catch { }
                }
                if (hit != null) break;
            }
            _memberCache[key] = hit;
            LogOnce(key, hit != null
                ? "FOUND " + t.Name + "." + hit.Name + " (" + (hit is PropertyInfo ? "prop" : "field") + ":" + MemberType(hit).Name + ") from [" + string.Join(",", candidates) + "]"
                : "MISS  " + t.Name + " none of [" + string.Join(",", candidates) + "]");
            return hit;
        }

        internal static Type MemberType(MemberInfo m)
        {
            var p = m as PropertyInfo; if (p != null) return p.PropertyType;
            var f = m as FieldInfo; if (f != null) return f.FieldType;
            return typeof(object);
        }

        internal static bool CanWrite(MemberInfo m)
        {
            var p = m as PropertyInfo; if (p != null) return p.CanWrite && p.GetSetMethod(true) != null;
            var f = m as FieldInfo; if (f != null) return !f.IsInitOnly && !f.IsLiteral;
            return false;
        }

        internal static bool TryGet(object target, MemberInfo m, out object value)
        {
            value = null;
            if (m == null) return false;
            try
            {
                var p = m as PropertyInfo;
                if (p != null) { value = p.GetValue(target, null); return true; }
                var f = m as FieldInfo;
                if (f != null) { value = f.GetValue(target); return true; }
            }
            catch (Exception ex)
            {
                LogOnce("getfail|" + m.DeclaringType?.FullName + "." + m.Name, "GET FAIL " + m.DeclaringType?.Name + "." + m.Name + ": " + Inner(ex).Message);
            }
            return false;
        }

        internal static bool TrySet(object target, MemberInfo m, object value)
        {
            if (m == null || !CanWrite(m)) return false;
            try
            {
                var p = m as PropertyInfo;
                if (p != null) { p.SetValue(target, value, null); return true; }
                var f = m as FieldInfo;
                if (f != null) { f.SetValue(target, value); return true; }
            }
            catch (Exception ex)
            {
                LogOnce("setfail|" + m.DeclaringType?.FullName + "." + m.Name, "SET FAIL " + m.DeclaringType?.Name + "." + m.Name + ": " + Inner(ex).Message);
            }
            return false;
        }

        private static Exception Inner(Exception ex)
        {
            var t = ex as TargetInvocationException;
            return t != null && t.InnerException != null ? t.InnerException : ex;
        }

        /// <summary>Find and invoke an instance method with one argument by candidate names. Returns true if invoked.</summary>
        internal static bool TryInvoke1(object target, object arg, Type argType, params string[] candidates)
        {
            if (target == null) return false;
            var t = target.GetType();
            string key = "m|" + t.FullName + "|" + (argType != null ? argType.FullName : "?") + "|" + string.Join(",", candidates);
            MemberInfo cached;
            MethodInfo mi = null;
            if (_memberCache.TryGetValue(key, out cached)) mi = cached as MethodInfo;
            else
            {
                foreach (var name in candidates)
                {
                    try
                    {
                        foreach (var m in t.GetMethods(Inst))
                        {
                            if (m.Name != name) continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 1) continue;
                            if (argType != null && !ps[0].ParameterType.IsAssignableFrom(argType)) continue;
                            mi = m; break;
                        }
                    }
                    catch { }
                    if (mi != null) break;
                }
                _memberCache[key] = mi;
                LogOnce(key, mi != null ? "FOUND method " + t.Name + "." + mi.Name + "(" + (argType != null ? argType.Name : "?") + ")"
                                        : "MISS  method " + t.Name + " none of [" + string.Join(",", candidates) + "]");
            }
            if (mi == null) return false;
            try { mi.Invoke(target, new[] { arg }); return true; }
            catch (Exception ex)
            {
                NOSaveSystemPlugin.Log?.LogWarning("NOSave reflect invoke " + t.Name + "." + mi.Name + " FAIL: " + Inner(ex).Message);
                return false;
            }
        }

        /// <summary>Resolve a game type by simple name across loaded assemblies (Assembly-CSharp first); cached.</summary>
        internal static Type FindType(params string[] simpleNames)
        {
            string key = string.Join(",", simpleNames);
            Type cached;
            if (_typeCache.TryGetValue(key, out cached)) return cached;
            Type hit = null;
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var name in simpleNames)
                {
                    foreach (var a in asms)
                    {
                        string an = null;
                        try { an = a.GetName().Name; } catch { }
                        if (an != "Assembly-CSharp") continue;
                        hit = SearchAsm(a, name);
                        if (hit != null) break;
                    }
                    if (hit != null) break;
                }
            }
            catch { }
            _typeCache[key] = hit;
            LogOnce("type|" + key, hit != null ? "FOUND type " + hit.FullName + " from [" + key + "]" : "MISS  type none of [" + key + "]");
            return hit;
        }

        private static Type SearchAsm(Assembly a, string name)
        {
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
            catch { return null; }
            if (types == null) return null;
            foreach (var t in types)
                if (t != null && t.Name == name) return t;
            return null;
        }

        // ---------- value encoding (string-typed property bag) ----------

        /// <summary>Encode primitive / enum / string; returns false for unsupported types.</summary>
        internal static bool Encode(object v, out string vtype, out string text)
        {
            vtype = null; text = null;
            if (v == null) return false;
            var t = v.GetType();
            if (t.IsEnum) { vtype = "e"; text = v.ToString(); return true; }
            if (v is bool) { vtype = "b"; text = ((bool)v) ? "true" : "false"; return true; }
            if (v is float || v is double || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                vtype = "f"; text = d.ToString("R", CultureInfo.InvariantCulture); return true;
            }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ushort || v is sbyte)
            { vtype = "i"; text = Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); return true; }
            if (v is ulong) { vtype = "i"; text = ((ulong)v).ToString(CultureInfo.InvariantCulture); return true; }
            if (v is string) { vtype = "s"; text = (string)v; return true; }
            return false;
        }

        /// <summary>Decode to the member's declared type. Returns false if not convertible.</summary>
        internal static bool Decode(string vtype, string text, Type want, out object value)
        {
            value = null;
            if (want == null || text == null) return false;
            try
            {
                if (want.IsEnum)
                {
                    value = Enum.Parse(want, text, true);
                    return true;
                }
                if (want == typeof(string)) { value = text; return true; }
                if (want == typeof(bool)) { value = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase); return true; }
                if (want == typeof(float) || want == typeof(double) || want == typeof(decimal) ||
                    want == typeof(int) || want == typeof(long) || want == typeof(short) || want == typeof(byte) ||
                    want == typeof(uint) || want == typeof(ushort) || want == typeof(sbyte) || want == typeof(ulong))
                {
                    // v0.9.1: integers parse exactly (via double, long/ulong lost precision above 2^53).
                    if (want == typeof(long) && vtype == "i") { value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture); return true; }
                    if (want == typeof(ulong) && vtype == "i") { value = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture); return true; }
                    double d = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (want == typeof(float) || want == typeof(double) || want == typeof(decimal))
                        value = Convert.ChangeType(d, want, CultureInfo.InvariantCulture);
                    else
                        value = Convert.ChangeType(Math.Round(d), want, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static bool IsSimple(Type t)
        {
            if (t == null) return false;
            return t.IsEnum || t == typeof(string) || t == typeof(bool) || t == typeof(float) || t == typeof(double) ||
                   t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) ||
                   t == typeof(ushort) || t == typeof(ulong) || t == typeof(sbyte) || t == typeof(decimal);
        }

        /// <summary>Enumerate IEnumerable member value safely (lists/arrays); null when not enumerable.</summary>
        internal static List<object> AsList(object v)
        {
            if (v == null || v is string) return null;
            var e = v as IEnumerable;
            if (e == null) return null;
            var list = new List<object>();
            try { foreach (var x in e) list.Add(x); } catch { return null; }
            return list;
        }
    }
}
