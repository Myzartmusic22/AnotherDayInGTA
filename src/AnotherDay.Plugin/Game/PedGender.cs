using System;
using GTA;
using GTA.Native;

namespace AnotherDay.Plugin.Game
{
    /// <summary>
    /// Classifies a pedestrian's sex from the live model.
    ///
    /// This is the value that decides which Gemini voice an NPC is allowed to
    /// use, so it must not be a guess from a name or a personality. The native
    /// asks the game about the entity's model; SHVDN's model flag is the
    /// fallback, and a person of unknown sex falls back to male, which is the
    /// only choice that can never put a woman's voice on a man's body in the
    /// other direction (both defaults are verified voices).
    /// </summary>
    internal static class PedGender
    {
        public static bool IsMale(Ped ped)
        {
            if (ped == null || !ped.Exists()) return true;

            try
            {
                return Function.Call<bool>(Hash.IS_PED_MALE, ped);
            }
            catch (Exception ex)
            {
                PluginLog.Debug("IS_PED_MALE failed, using the model flag: " + ex.Message);
            }

            try
            {
                return ped.Model.IsMalePed;
            }
            catch (Exception ex)
            {
                PluginLog.Debug("Model gender flags unavailable: " + ex.Message);
            }

            return true;
        }

        /// <summary>The same question for a ped that may already be gone.</summary>
        public static bool IsMaleSafe(Ped ped)
        {
            try { return IsMale(ped); }
            catch { return true; }
        }
    }
}
