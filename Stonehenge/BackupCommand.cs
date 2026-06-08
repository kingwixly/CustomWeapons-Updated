using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace CustomWeapons.Stonehenge
{
	public class BackupCommand : MonoBehaviour
	{
		private class TargetData
		{
			public Vector3 pos;
			public Vector3 vel;
			public Vector3 error;

			public TargetData(Vector3 pos, Vector3 vel, Vector3 error)
			{
				this.pos = pos;
				this.vel = vel;
				this.error = error;
			}
		}
		
		[SerializeField] private StonehengeControl control;
		[SerializeField] private float maxError;
		[SerializeField] private TargetDetector targetDetector;
		[SerializeField] private float targetAssessmentInterval;
		[SerializeField] private Unit attachedUnit;


		private Unit target;
		private Dictionary<Unit, TargetData> targets = new Dictionary<Unit, TargetData>();
		private float lastTargetAssesment;
		private float priorityThreshold;
		private WeaponStation weaponStation;

		private void Awake()
		{
			if (targetDetector != null)
			{
				targetDetector.onDetectTarget += BackupCommand_OnDetectTarget;
				targetDetector.onScan += BackupCommand_OnCompleteScan;
			}

			// Guard against `control` being unwired in the prefab: without it
			// this component has no turret to drive and may as well disable itself
			// before FixedUpdate starts dereferencing nulls.
			if (control != null && control.turret != null)
			{
				weaponStation = control.turret.GetWeaponStation();
			}
			else
			{
				base.enabled = false;
			}
		}

		private void FixedUpdate()
		{
			if (attachedUnit == null || attachedUnit.disabled || weaponStation == null)
			{
				base.enabled = false;
				return;
			}

			TargetData data;
			if ( target != null && targets.TryGetValue(target, out data))
			{
				control.Aim(data.pos + data.error, data.vel);

				if (control.IsOnTarget())
				{
					weaponStation.Fire(attachedUnit, target);
				}
			}
		}

		private void BackupCommand_OnDetectTarget(Unit unit)
		{
			if (targets.ContainsKey(unit)) return;
			if (unit.rb != null)
			{
				Vector3 error = Random.insideUnitSphere * maxError;
				targets.Add(unit, new TargetData(unit.transform.position, unit.rb.velocity, error));
			}
		}

		private void BackupCommand_OnCompleteScan()
		{
			var toRemove = new List<Unit>();
			foreach (var kvp in targets)
			{
				var unit = kvp.Key;
				if (unit != null && targetDetector.detectedTargets.Contains(unit))
				{
					targets[unit].pos = unit.transform.position;
					targets[unit].vel = unit.rb?.velocity ?? Vector3.zero;
				}
				else
				{
					toRemove.Add(unit);
				}
			}
			foreach (var unit in toRemove) targets.Remove(unit);
			if (Time.timeSinceLevelLoad - lastTargetAssesment > targetAssessmentInterval)
			{
				ChooseTarget(true);
			}
		}

		private void BackupCommand_OnTargetDisabled(Unit unit)
		{
			target = null;
			targets.Remove(unit);
		}

		private void OnDestroy()
		{
			// Mirror the Awake subscriptions so the event handlers don't leak.
			if (targetDetector != null)
			{
				targetDetector.onDetectTarget -= BackupCommand_OnDetectTarget;
				targetDetector.onScan -= BackupCommand_OnCompleteScan;
			}
			if (target != null)
			{
				target.onDisableUnit -= BackupCommand_OnTargetDisabled;
			}
		}

		private void ChooseTarget(bool clearAfterSearch)
		{
			if (attachedUnit == null || attachedUnit.disabled)
			{
				//how
				return;
			}

			lastTargetAssesment = Time.timeSinceLevelLoad;
			if (target != null)
			{
				target.onDisableUnit -= BackupCommand_OnTargetDisabled;
				if (attachedUnit.NetworkHQ != null &&
				    attachedUnit.NetworkHQ.trackingDatabase != null &&
				    attachedUnit.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out var value))
				{
					value.attackers--;
				}
			}
			Unit unit = target;
			target = null;
			priorityThreshold = 0f;
			foreach (Unit potentialTarget in targets.Keys)
			{
				if (potentialTarget == null) continue;
				TargetData data;
				if (targets.TryGetValue(potentialTarget, out data))
				{
					AssessTargetPriority(potentialTarget, data);
				}
				
			}
			if (target != null)
			{
				target.onDisableUnit += BackupCommand_OnTargetDisabled;
				if (attachedUnit.NetworkHQ != null &&
				    attachedUnit.NetworkHQ.trackingDatabase != null &&
				    attachedUnit.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out var v))
				{
					v.attackers++;
				}
			}

			// Use a plain array instead of stackalloc Span<PersistentID> so this
			// compiles cleanly against the project's net472 / LangVersion 9 setup.
			// RpcSetStationTargets accepts PersistentID[] which is equivalent for
			// this single-element call.
			PersistentID[] ids = new PersistentID[] { (target != null) ? target.persistentID : PersistentID.None };

			if (target != unit && weaponStation != null)
			{
				attachedUnit.RpcSetStationTargets(weaponStation.Number, ids);
			}
		}

		private void AssessTargetPriority(Unit targetCandidate, TargetData data)
		{
			// Null-guard everything before any field access. The original ordered
			// this check after dereferencing `targetCandidate.persistentID`, which
			// would NRE the moment any candidate had been freed mid-scan.
			if (targetCandidate == null || targetCandidate.disabled ||
			    targetCandidate.NetworkHQ == null || attachedUnit == null || attachedUnit.disabled ||
			    attachedUnit.NetworkHQ == null)
			{
				return;
			}

			TrackingInfo trackingData = attachedUnit.NetworkHQ.GetTrackingData(targetCandidate.persistentID);
			if (trackingData == null)
			{
				return;
			}

			float num = FastMath.Distance(data.pos, base.transform.position);
			if (num <= 0f) return;
			float num2 = weaponStation.WeaponInfo.targetRequirements.maxRange / num;
			if (!(num2 < 0.7f))
			{
				OpportunityThreat opportunityThreat =
					CombatAI.AnalyzeTarget(weaponStation, attachedUnit, trackingData, 2f, num);
				float num3 = opportunityThreat.opportunity * (1f + opportunityThreat.threat) * num2;
				if (num3 != 0f)
				{
					if (weaponStation.Reloading || weaponStation.Ammo <= 0)
					{
						num3 = 0.01f;
					}

					if (num > weaponStation.WeaponInfo.targetRequirements.maxRange ||
					    num < weaponStation.WeaponInfo.targetRequirements.minRange)
					{
						num3 *= 0.01f;
					}

					if (targetCandidate.speed > 0f && num2 < 2f)
					{
						num3 *= 0.6f;
					}

					if (num3 > priorityThreshold)
					{
						target = targetCandidate;
						priorityThreshold = num3;
					}
				}
			}
		}
	}
}
