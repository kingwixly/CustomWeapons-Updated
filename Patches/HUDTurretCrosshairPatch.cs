using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CustomWeapons.Patches
{
    [HarmonyPatch(typeof(HUDTurretCrosshair))]
    public static class HUDTurretCrosshairPatch
    {
        static readonly AccessTools.FieldRef<HUDTurretCrosshair, Turret> TurretRef =
            AccessTools.FieldRefAccess<HUDTurretCrosshair, Turret>("turret");

        static readonly AccessTools.FieldRef<HUDTurretCrosshair, Gun> GunRef =
            AccessTools.FieldRefAccess<HUDTurretCrosshair, Gun>("gun");

        static readonly AccessTools.FieldRef<HUDTurretCrosshair, Image> CrosshairRef =
            AccessTools.FieldRefAccess<HUDTurretCrosshair, Image>("crosshair");

        static readonly AccessTools.FieldRef<HUDTurretCrosshair, Image> CircleRef =
            AccessTools.FieldRefAccess<HUDTurretCrosshair, Image>("circle");

        static readonly AccessTools.FieldRef<HUDTurretCrosshair, Image> ReadinessCircleRef =
            AccessTools.FieldRefAccess<HUDTurretCrosshair, Image>("readinessCircle");

        [HarmonyPatch("Refresh")]
        [HarmonyPrefix]
        public static bool RefreshPrefix(
            HUDTurretCrosshair __instance,
            Camera mainCamera,
            out Vector3 crosshairPosition)
        {
            Debug.Log("[Test] Applying CrosshairPatch");
            crosshairPosition = Vector3.one * 10000f;

            Turret turret = TurretRef(__instance);
            if (turret == null)
                return false;

            Gun gun = GunRef(__instance);
            Image crosshair = CrosshairRef(__instance);
            Image circle = CircleRef(__instance);
            Image readinessCircle = ReadinessCircleRef(__instance);

            Vector3 direction = turret.GetDirection();
            bool onTarget = turret.IsOnTarget();

            if (Vector3.Dot(mainCamera.transform.forward,
                            direction - mainCamera.transform.position) > 0f)
            {
                crosshairPosition =
                    SceneSingleton<CameraStateManager>.i.mainCamera
                        .WorldToScreenPoint(direction);

                crosshairPosition.z = 0f;
                __instance.transform.position = crosshairPosition;
                crosshair.enabled = true;

                float reloadProgress = gun != null
                    ? gun.GetReloadProgress()
                    : 0f;

                if (gun != null && reloadProgress > 0f)
                {
                    if (!readinessCircle.enabled)
                    {
                        readinessCircle.enabled = true;
                        crosshair.color = Color.red + Color.green * 0.5f;
                    }

                    readinessCircle.fillAmount = reloadProgress;
                }
                else
                {
                    if (readinessCircle.enabled)
                    {
                        readinessCircle.enabled = false;
                        crosshair.color = Color.green;
                    }
                }
                
                circle.enabled = onTarget && reloadProgress <= 0f;
            }
            else
            {
                circle.enabled = false;
                readinessCircle.enabled = false;
                crosshair.enabled = false;
            }

            return false;
        }
    }
}
