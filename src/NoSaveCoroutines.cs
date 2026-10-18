using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NOSaveSystem
{
    internal static class NoSaveWait
    {
        internal static bool Paused
        {
            get
            {
                try { if (Time.timeScale <= 0f) return true; } catch { }
                try { return GameplayUI.GameIsPaused; } catch { return false; }
            }
        }

        internal static float Dt()
        {
            float d = Time.unscaledDeltaTime;
            return Paused ? d * PausedFactor : d;
        }
        internal const float PausedFactor = 1f / 120f;

        internal static void Timeout(string name, float seconds, string fallback)
        {
            NOSaveSystemPlugin.Log?.LogWarning("NOSAVE-WAIT " + name + " TIMEOUT after " + seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + "s -> fallback: " + fallback);
        }
    }

    internal static class NoSaveCoroutines
    {
        internal static IEnumerator Safe(IEnumerator root, string label, Action<Exception> onError = null)
        {
            if (root == null) yield break;
            var stack = new Stack<IEnumerator>(4);
            stack.Push(root);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                bool moved = false;
                object current = null;
                Exception error = null;
                try
                {
                    moved = top.MoveNext();
                    if (moved) current = top.Current;
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (error != null)
                {
                    stack.Pop();
                    NOSaveSystemPlugin.Log?.LogError("NOSave coroutine '" + label + "' threw: " + error);
                    while (stack.Count > 0)
                    {
                        var parent = stack.Pop();
                        try { (parent as IDisposable)?.Dispose(); }
                        catch (Exception dex) { NOSaveSystemPlugin.Log?.LogWarning("NOSave dispose after error: " + dex.Message); }
                    }
                    try { onError?.Invoke(error); } catch { }
                    yield break;
                }

                if (!moved)
                {
                    stack.Pop();
                    continue;
                }

                var nested = current as IEnumerator;
                if (nested != null)
                {
                    stack.Push(nested);
                    continue;
                }
                yield return current;
            }
        }
    }
}
