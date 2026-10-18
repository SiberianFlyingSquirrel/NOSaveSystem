using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NOSaveSystem
{
    /// <summary>
    /// v0.9.0: exception-safe coroutine driver.
    /// Unity stops a coroutine when a nested IEnumerator throws and never resumes or disposes the parents,
    /// so parent finally blocks (lock release, unpause, busy flag) never ran -> stuck "busy" / stuck pause.
    /// This flattens nested IEnumerators, catches exceptions from every MoveNext, and Disposes the suspended
    /// parents on error so their finally blocks run. Non-IEnumerator yields (null, WaitForSeconds, ...) pass through.
    /// </summary>
    /// <summary>v0.11.0: uniform log line for every capped wait / loop on the load path.</summary>
    internal static class NoSaveWait
    {
        /// <summary>v0.11.0 (PAUSE0): true while the game is paused (pause menu or timeScale 0).</summary>
        internal static bool Paused
        {
            get
            {
                try { if (Time.timeScale <= 0f) return true; } catch { }
                try { return GameplayUI.GameIsPaused; } catch { return false; }
            }
        }

        /// <summary>
        /// v0.11.0 (PAUSE0): delta for realtime caps that must not fire spuriously while paused.
        /// Unpaused: Time.unscaledDeltaTime. Paused: 1/120 of it, so a cap of X s can stretch to at most 120*X s of
        /// realtime while the player sits in the pause menu (still bounded; never an infinite wait).
        /// </summary>
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
                    stack.Pop(); // the thrower already unwound its own finally blocks
                    NOSaveSystemPlugin.Log?.LogError("NOSave coroutine '" + label + "' threw: " + error);
                    while (stack.Count > 0)
                    {
                        var parent = stack.Pop();
                        try { (parent as IDisposable)?.Dispose(); } // runs the parent's pending finally blocks
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
