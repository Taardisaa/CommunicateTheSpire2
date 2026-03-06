using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CommunicateTheSpire2;

/// <summary>
/// Example Harmony patch: runs after the game's ModManager.Initialize().
/// Shows that you can hook game code using only the game's DLLs (no source build).
/// </summary>
[HarmonyPatch(typeof(ModManager), nameof(ModManager.Initialize))]
public static class PatchModManagerInitialize
{
	[HarmonyPostfix]
	public static void Postfix()
	{
		CommunicateTheSpireLog.Write("Harmony postfix ran after ModManager.Initialize — hooking works.");
		Log.Info("CommunicateTheSpire2: Harmony postfix ran after ModManager.Initialize — hooking works.");
	}
}

