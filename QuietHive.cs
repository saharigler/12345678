using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace QuietHive
{
    [DefOf]
    public static class QuietHiveDefOf
    {
        public static HediffDef QuietHive_Infection;
        public static JobDef QuietHive_CovertInfect;

        static QuietHiveDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(QuietHiveDefOf));
        }
    }

    public class Hediff_QuietHiveInfection : HediffWithComps
    {
        public HediffComp_QuietHiveMind Mind => this.TryGetComp<HediffComp_QuietHiveMind>();
    }

    public class HediffCompProperties_QuietHiveMind : HediffCompProperties
    {
        public HediffCompProperties_QuietHiveMind()
        {
            compClass = typeof(HediffComp_QuietHiveMind);
        }
    }

    public class HediffComp_QuietHiveMind : HediffComp
    {
        public float suspicion;
        public int nextAttemptTick;
        public int infectionsCaused;

        public override void CompExposeData()
        {
            base.CompExposeData();
            Scribe_Values.Look(ref suspicion, "quietHiveSuspicion", 0f);
            Scribe_Values.Look(ref nextAttemptTick, "quietHiveNextAttemptTick", 0);
            Scribe_Values.Look(ref infectionsCaused, "quietHiveInfectionsCaused", 0);
        }

        public override void CompPostTick(ref float severityAdjustment)
        {
            base.CompPostTick(ref severityAdjustment);
            if (suspicion > 0f && Find.TickManager.TicksGame % 600 == 0)
                suspicion = Math.Max(0f, suspicion - 0.015f);
        }
    }

    public class GameComponent_QuietHive : GameComponent
    {
        public GameComponent_QuietHive(Game game) { }

        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % 250 != 0) return;

            foreach (Map map in Find.Maps)
            {
                List<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                int hiveSize = pawns.Count(IsInfected);

                foreach (Pawn host in pawns.ToList())
                {
                    if (!CanHostAct(host)) continue;

                    Hediff_QuietHiveInfection infection = host.health.hediffSet
                        .GetFirstHediffOfDef(QuietHiveDefOf.QuietHive_Infection) as Hediff_QuietHiveInfection;
                    HediffComp_QuietHiveMind mind = infection?.Mind;
                    if (mind == null || now < mind.nextAttemptTick) continue;

                    // Suspicious hosts deliberately act normal for a while.
                    if (mind.suspicion >= 0.65f)
                    {
                        mind.nextAttemptTick = now + Rand.RangeInclusive(5000, 9000);
                        continue;
                    }

                    Pawn target = PickTarget(host, map, hiveSize);
                    if (target == null)
                    {
                        mind.nextAttemptTick = now + Rand.RangeInclusive(1200, 2400);
                        continue;
                    }

                    int witnesses = CountWitnesses(host, target);
                    int allowedWitnesses = hiveSize >= 8 && mind.suspicion < 0.15f ? 1 : 0;
                    if (witnesses > allowedWitnesses)
                    {
                        mind.nextAttemptTick = now + Rand.RangeInclusive(900, 1800);
                        continue;
                    }

                    Job job = JobMaker.MakeJob(QuietHiveDefOf.QuietHive_CovertInfect, target);
                    host.jobs.StartJob(job, JobCondition.InterruptOptional);
                    mind.nextAttemptTick = now + Rand.RangeInclusive(3500, 6500);
                }
            }
        }

        private static bool CanHostAct(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted) return false;
            if (pawn.InMentalState || pawn.jobs == null || pawn.Map == null) return false;
            if (!pawn.RaceProps.Humanlike || !IsInfected(pawn)) return false;

            Job cur = pawn.CurJob;
            if (cur != null && (cur.def == JobDefOf.AttackMelee || cur.def == JobDefOf.AttackStatic || cur.def == QuietHiveDefOf.QuietHive_CovertInfect))
                return false;
            return true;
        }

        public static bool IsInfected(Pawn pawn)
        {
            return pawn?.health?.hediffSet?.HasHediff(QuietHiveDefOf.QuietHive_Infection) == true;
        }

        private static Pawn PickTarget(Pawn host, Map map, int hiveSize)
        {
            Pawn best = null;
            float bestScore = float.MinValue;

            foreach (Pawn target in map.mapPawns.AllPawnsSpawned)
            {
                if (target == host || target.Dead || IsInfected(target)) continue;
                if (!target.RaceProps.Humanlike || target.HostileTo(host)) continue;
                if (!host.CanReach(target, PathEndMode.Touch, Danger.Some)) continue;

                int witnesses = CountWitnesses(host, target);
                if (witnesses > 0 && hiveSize < 8) continue;

                float distance = host.Position.DistanceTo(target.Position);
                float score = 30f - distance;

                if (target.Downed) score += 28f;
                if (target.CurJobDef == JobDefOf.LayDown) score += 22f;
                if (!target.Awake()) score += 18f;
                Room room = target.Position.GetRoom(map);
                if (room != null && !room.PsychologicallyOutdoors) score += 5f;
                score -= witnesses * 35f;

                // Even without direct witnesses, avoid busy rooms and crowds.
                int nearby = map.mapPawns.AllPawnsSpawned.Count(p =>
                    p != host && p != target && !p.Dead && !p.Downed &&
                    p.RaceProps.Humanlike && p.Position.DistanceTo(target.Position) <= 8f);
                score -= nearby * 4f;

                // A sleeping target in a quiet bedroom is an ideal victim.
                if (!target.Awake() && nearby == 0) score += 12f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = target;
                }
            }

            return bestScore >= 8f ? best : null;
        }

        public static int CountWitnesses(Pawn host, Pawn target)
        {
            if (host?.Map == null || target?.Map != host.Map) return 99;

            int count = 0;
            foreach (Pawn witness in host.Map.mapPawns.AllPawnsSpawned)
            {
                if (witness == host || witness == target || witness.Dead || witness.Downed) continue;
                if (!witness.RaceProps.Humanlike || !witness.Awake()) continue;
                if (IsInfected(witness)) continue; // Hive members protect one another.
                if (witness.Position.DistanceTo(target.Position) > 16f) continue;
                if (GenSight.LineOfSight(witness.Position, target.Position, host.Map)) count++;
            }
            return count;
        }
    }

    public class JobDriver_CovertInfect : JobDriver
    {
        private Pawn Victim => job.GetTarget(TargetIndex.A).Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Victim, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => Victim == null || Victim.Dead || GameComponent_QuietHive.IsInfected(Victim));

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            // The innocuous-looking pause makes the host appear to be merely spending time nearby.
            Toil wait = Toils_General.Wait(180, TargetIndex.A);
            wait.WithProgressBarToilDelay(TargetIndex.A);
            yield return wait;

            Toil infect = ToilMaker.MakeToil("QuietHive_Infect");
            infect.initAction = delegate
            {
                Pawn victim = Victim;
                Hediff_QuietHiveInfection own = pawn.health.hediffSet
                    .GetFirstHediffOfDef(QuietHiveDefOf.QuietHive_Infection) as Hediff_QuietHiveInfection;
                HediffComp_QuietHiveMind mind = own?.Mind;

                if (victim == null || victim.Dead || GameComponent_QuietHive.IsInfected(victim)) return;

                int witnesses = GameComponent_QuietHive.CountWitnesses(pawn, victim);
                if (witnesses > 0)
                {
                    if (mind != null)
                    {
                        mind.suspicion = Math.Min(1f, mind.suspicion + 0.18f * witnesses);
                        mind.nextAttemptTick = Find.TickManager.TicksGame + Rand.RangeInclusive(3500, 7000);
                    }
                    return; // Somebody arrived: abort and act normal.
                }

                victim.health.AddHediff(QuietHiveDefOf.QuietHive_Infection);
                if (mind != null)
                {
                    mind.infectionsCaused++;
                    mind.suspicion = Math.Max(0f, mind.suspicion - 0.08f);
                }

                // Intentionally subtle: no dramatic infection message or attack notification.
                MoteMaker.ThrowText(victim.DrawPos, victim.Map, "...", 0.55f);
            };
            infect.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return infect;
        }
    }
}
