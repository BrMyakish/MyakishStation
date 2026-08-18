// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Linq;
using System.Text;
using Content.Goobstation.Shared.Disease.Components;
using Content.Server.Access.Systems;
using Content.Server.Body.Systems;
using Content.Server.GameTicking;
using Content.Server.Medical.Components;
using Content.Server.MedicalRecords;
using Content.Server.Popups;
using Content.Server.Radio.EntitySystems;
using Content.Server.Station.Systems;
using Content.Server.StationRecords.Systems;
using Content.Shared.Access;
using Content.Shared.Access.Systems;
using Content.Shared.Atmos.Rotting;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.GameTicking;
using Content.Shared.IdentityManagement;
using Content.Shared.MedicalScanner;
using Content.Shared.MedicalRecords;
using Content.Shared.MedicalRecords.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.PowerCell;
using Content.Shared.StationRecords;
using Content.Shared.Temperature.Components;
using Content.Shared.Traits.Assorted;
using Content.Shared._Shitmed.Medical.HealthAnalyzer;
using Content.Shared._Shitmed.Medical.Surgery.Steps.Parts;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Components;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Systems;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.Medical;

/// <summary>
/// Uploads the currently active health analyzer scan into the patient's station medical record.
/// </summary>
public sealed class HealthAnalyzerMedicalRecordsSystem : EntitySystem
{
    private static readonly ProtoId<AccessLevelPrototype> MedicalAccess = "Medical";

    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly BloodstreamSystem _bloodstream = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly IdCardSystem _idCard = default!;
    [Dependency] private readonly MedicalRecordsSystem _medicalRecords = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MobThresholdSystem _threshold = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly StationRecordsSystem _records = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly TraumaSystem _trauma = default!;
    [Dependency] private readonly PowerCellSystem _power = default!;
    [Dependency] private readonly WoundSystem _wound = default!;

    private readonly Dictionary<StationRecordKey, TimeSpan> _cooldowns = new();

    public override void Initialize()
    {
        Subs.BuiEvents<HealthAnalyzerComponent>(HealthAnalyzerUiKey.Key, subs =>
        {
            subs.Event<HealthAnalyzerUploadToMedicalRecordMessage>(OnUpload);
        });

        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _cooldowns.Clear());
    }

    private void OnUpload(Entity<HealthAnalyzerComponent> ent, ref HealthAnalyzerUploadToMedicalRecordMessage msg)
    {
        var user = msg.Actor;
        if (ent.Comp.ScannedBy != user || !_access.FindAccessTags(user).Contains(MedicalAccess))
        {
            Popup(ent, user, "health-analyzer-medical-record-no-access");
            return;
        }

        if (ent.Comp.ScannedEntity is not { } target || Deleted(target) ||
            !HasComp<BodyComponent>(target) || !_power.HasDrawCharge(ent.Owner, user: user))
        {
            Popup(ent, user, "health-analyzer-medical-record-no-active-scan");
            return;
        }

        if (ent.Comp.MaxScanRange is { } range &&
            !_transform.InRange(Transform(target).Coordinates, Transform(ent).Coordinates, range))
        {
            Popup(ent, user, "health-analyzer-medical-record-out-of-range");
            return;
        }

        if (!TryGetIdRecord(target, out var patientKey, out var patient))
        {
            Popup(ent, user, "health-analyzer-medical-record-missing-id");
            return;
        }

        var examinerName = Identity.Name(user, EntityManager);
        var examinerJob = Loc.GetString("medical-records-report-unknown");
        if (_idCard.TryFindIdCard(user, out var examinerId))
        {
            if (examinerId.Comp.FullName is { } fullName && !string.IsNullOrWhiteSpace(fullName))
                examinerName = fullName;
            if (examinerId.Comp.LocalizedJobTitle is { } jobTitle && !string.IsNullOrWhiteSpace(jobTitle))
                examinerJob = jobTitle;
        }

        if (_cooldowns.TryGetValue(patientKey, out var nextUpload) && nextUpload > _timing.CurTime)
        {
            var seconds = Math.Max(1, (int) Math.Ceiling((nextUpload - _timing.CurTime).TotalSeconds));
            _popup.PopupEntity(
                Loc.GetString("health-analyzer-medical-record-cooldown", ("seconds", seconds)),
                ent,
                user,
                PopupType.MediumCaution);
            return;
        }

        var report = BuildReport(target, patient, examinerName, examinerJob);
        var title = Loc.GetString("medical-records-default-examination-title",
            ("time", _ticker.RoundDuration().ToString("hh\\:mm\\:ss")));

        if (!_medicalRecords.TryAddExamination(
                patientKey,
                title,
                examinerName,
                examinerJob,
                report))
        {
            Popup(ent, user, "health-analyzer-medical-record-save-failed");
            return;
        }

        // Rejections and errors above never consume the cooldown.
        _cooldowns[patientKey] = _timing.CurTime + ent.Comp.MedicalRecordUploadCooldown;

        var radioMessage = Loc.GetString("health-analyzer-medical-record-radio",
            ("examiner", examinerName),
            ("examinerJob", examinerJob),
            ("patient", patient.Name),
            ("patientJob", patient.JobTitle));
        if (TryFindMedicalRecordsConsole(patientKey.OriginStation, out var console))
            _radio.SendRadioMessage(console, radioMessage, ent.Comp.MedicalRecordChannel, console);

        Popup(ent, user, "health-analyzer-medical-record-saved", PopupType.Medium);
    }

    private bool TryFindMedicalRecordsConsole(EntityUid station, out EntityUid console)
    {
        var query = EntityQueryEnumerator<MedicalRecordsConsoleComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (_station.GetOwningStation(uid) != station)
                continue;

            console = uid;
            return true;
        }

        console = default;
        return false;
    }

    private bool TryGetIdRecord(
        EntityUid entity,
        out StationRecordKey key,
        out GeneralStationRecord record)
    {
        key = default;
        record = default!;

        if (!_idCard.TryFindIdCard(entity, out var idCard) ||
            !TryComp<StationRecordKeyStorageComponent>(idCard.Owner, out var storage) ||
            storage.Key is not { } storedKey ||
            !_records.TryGetRecord(storedKey, out GeneralStationRecord? foundRecord))
        {
            return false;
        }

        key = storedKey;
        record = foundRecord;
        return true;
    }

    private string BuildReport(
        EntityUid target,
        GeneralStationRecord patient,
        string examinerName,
        string examinerJob)
    {
        var report = new StringBuilder();
        report.AppendLine(Loc.GetString("medical-records-report-title"));
        report.AppendLine(Loc.GetString("medical-records-report-time",
            ("time", _ticker.RoundDuration().ToString("hh\\:mm\\:ss"))));
        report.AppendLine(Loc.GetString("medical-records-report-patient",
            ("name", patient.Name), ("job", patient.JobTitle)));
        report.AppendLine(Loc.GetString("medical-records-report-examiner",
            ("name", examinerName), ("job", examinerJob)));

        AppendStatus(report, target);
        AppendDamage(report, target);
        AppendWounds(report, target);
        AppendDiseases(report, target);
        AppendOrgans(report, target);
        AppendChemicals(report, target);

        return report.ToString().TrimEnd();
    }

    private void AppendStatus(StringBuilder report, EntityUid target)
    {
        report.AppendLine();
        report.AppendLine(Loc.GetString("medical-records-report-status-heading"));

        var mobState = _mobState.IsDead(target)
            ? Loc.GetString("health-analyzer-window-entity-dead-text")
            : _mobState.IsCritical(target)
                ? Loc.GetString("health-analyzer-window-entity-critical-text")
                : _mobState.IsAlive(target)
                    ? Loc.GetString("health-analyzer-window-entity-alive-text")
                    : Loc.GetString("health-analyzer-window-entity-unknown-text");
        report.AppendLine($"{Loc.GetString("health-analyzer-window-entity-status-text")} {mobState}");

        if (TryComp<TemperatureComponent>(target, out var temperature))
        {
            report.AppendLine($"{Loc.GetString("health-analyzer-window-entity-temperature-text")} " +
                              $"{temperature.CurrentTemperature - 273.15f:F1} °C " +
                              $"({temperature.CurrentTemperature:F1} K)");
        }

        if (TryComp<BloodstreamComponent>(target, out var bloodstream))
        {
            var bloodLevel = _bloodstream.GetBloodLevel(target);
            report.AppendLine($"{Loc.GetString("health-analyzer-window-entity-blood-level-text")} {bloodLevel * 100:F1} %");

            if (bloodLevel < bloodstream.BloodlossThreshold)
            {
                report.AppendLine(Loc.GetString("condition-body-low-blood",
                    ("entity", Identity.Name(target, EntityManager))));
            }
        }

        if (TryComp<BlindableComponent>(target, out var blindable))
        {
            var weldingDamage = Math.Max(0, blindable.EyeDamage - blindable.MinDamage);
            if (weldingDamage > 0)
                report.AppendLine(Loc.GetString("health-analyzer-condition-welding-blindness"));
        }
    }

    private void AppendDamage(StringBuilder report, EntityUid target)
    {
        report.AppendLine();
        report.AppendLine(Loc.GetString("medical-records-report-damage-heading"));

        if (!TryComp<DamageableComponent>(target, out var damageable))
        {
            report.AppendLine(Loc.GetString("medical-records-report-none"));
            return;
        }

        report.AppendLine($"{Loc.GetString("health-analyzer-window-entity-damage-total-text")} {damageable.TotalDamage}");
        report.AppendLine($"{Loc.GetString("health-analyzer-window-entity-damage-vital-text")} " +
                          $"{_threshold.CheckVitalDamage(target, damageable)}");

        foreach (var (group, amount) in damageable.DamagePerGroup.OrderByDescending(x => x.Value))
        {
            if (amount <= 0)
                continue;

            var groupName = _prototypes.TryIndex(group, out DamageGroupPrototype? groupPrototype)
                ? groupPrototype.LocalizedName
                : group;
            report.AppendLine(Loc.GetString("health-analyzer-window-damage-group-text",
                ("damageGroup", groupName), ("amount", amount)));

            if (groupPrototype == null)
                continue;

            foreach (var type in groupPrototype.DamageTypes)
            {
                if (!damageable.Damage.DamageDict.TryGetValue(type, out var typeAmount) || typeAmount <= 0)
                    continue;

                string typeName = _prototypes.TryIndex(type, out DamageTypePrototype? typePrototype)
                    ? typePrototype.LocalizedName
                    : type.Id;
                report.AppendLine(" · " + Loc.GetString("health-analyzer-window-damage-type-text",
                    ("damageType", typeName), ("amount", typeAmount)));
            }
        }
    }

    private void AppendWounds(StringBuilder report, EntityUid target)
    {
        report.AppendLine();
        report.AppendLine(Loc.GetString("medical-records-report-wounds-heading"));

        if (!TryComp<BodyComponent>(target, out var body) || body.RootContainer.ContainedEntity is not { } root)
        {
            report.AppendLine(Loc.GetString("medical-records-report-none"));
            return;
        }

        var any = false;
        var targetName = Identity.Name(target, EntityManager);

        if (TryComp<UnrevivableComponent>(target, out var unrevivable) && unrevivable.Analyzable)
        {
            report.AppendLine(Loc.GetString("condition-body-unrevivable", ("entity", targetName)));
            any = true;
        }

        foreach (var (woundable, component) in _wound.GetAllWoundableChildren(root))
        {
            var bodyPart = _body.GetTargetBodyPart(woundable);
            var woundableName = Identity.Name(woundable, EntityManager);

            if (component.Bleeds > 0)
            {
                report.AppendLine(Loc.GetString($"condition-body-bleeding-{bodyPart}", ("entity", targetName)));
                any = true;
            }

            if (HasComp<IncisionOpenComponent>(woundable))
            {
                report.AppendLine(Loc.GetString($"health-analyzer-condition-open-incision-{bodyPart}"));
                any = true;
            }

            if (!_trauma.TryGetWoundableTrauma(woundable, out var traumas))
                continue;

            foreach (var trauma in traumas)
            {
                string traumaText;
                if (trauma.Comp.TargetType is { } targetType)
                {
                    traumaText = Loc.GetString($"condition-body-trauma-{trauma.Comp.TraumaType}",
                        ("targetSymmetry", targetType.Item2 != BodyPartSymmetry.None
                            ? $"{targetType.Item2.ToString().ToLower()} "
                            : string.Empty),
                        ("targetType", targetType.Item1.ToString().ToLower()));
                }
                else if (trauma.Comp.TraumaType == TraumaSystem.BoneDamage &&
                         trauma.Comp.TraumaTarget is { } boneWoundable &&
                         TryComp<BoneComponent>(boneWoundable, out var bone))
                {
                    traumaText = Loc.GetString(
                        $"condition-body-trauma-{trauma.Comp.TraumaType}-{bone.BoneSeverity}",
                        ("woundable", woundableName));
                }
                else
                {
                    traumaText = Loc.GetString($"condition-body-trauma-{trauma.Comp.TraumaType}",
                        ("woundable", woundableName));
                }

                report.AppendLine(traumaText);
                any = true;
            }
        }

        if (!any)
            report.AppendLine(Loc.GetString("condition-none"));
    }

    private void AppendDiseases(StringBuilder report, EntityUid target)
    {
        report.AppendLine();
        report.AppendLine(Loc.GetString("medical-records-report-diseases-heading"));

        if (!TryComp<DiseaseCarrierComponent>(target, out var carrier) || carrier.Diseases.ContainedEntities.Count == 0)
        {
            report.AppendLine(Loc.GetString("medical-records-report-none"));
            return;
        }

        var any = false;
        foreach (var diseaseUid in carrier.Diseases.ContainedEntities)
        {
            if (!TryComp<DiseaseComponent>(diseaseUid, out var disease))
                continue;

            report.AppendLine(Loc.GetString("health-analyzer-window-disease-type-text",
                ("type", disease.Genotype)));
            report.AppendLine(" · " + Loc.GetString("health-analyzer-window-disease-progress-text",
                ("progress", disease.InfectionProgress)));
            report.AppendLine(" · " + Loc.GetString("health-analyzer-window-immunity-progress-text",
                ("progress", disease.ImmunityProgress)));
            any = true;
        }

        if (!any)
            report.AppendLine(Loc.GetString("medical-records-report-none"));
    }

    private void AppendOrgans(StringBuilder report, EntityUid target)
    {
        report.AppendLine();
        report.AppendLine(Loc.GetString("medical-records-report-organs-heading"));

        var any = false;
        foreach (var (organUid, organ) in _body.GetBodyOrgans(target))
        {
            if (organ.IntegrityCap == 0)
                continue;

            var percent = organ.OrganIntegrity / organ.IntegrityCap * 100;
            var organName = Identity.Name(organUid, EntityManager);
            report.AppendLine(Loc.GetString("group-organ-status",
                ("organ", organName), ("capacity", percent)));

            if (HasComp<RottingComponent>(organUid))
                report.AppendLine(Loc.GetString("condition-organ-rotting", ("organ", organName)));

            report.AppendLine(Loc.GetString($"condition-organ-damage-{organ.OrganSeverity}",
                ("organ", organName)));
            any = true;
        }

        if (!any)
            report.AppendLine(Loc.GetString("medical-records-report-none"));
    }

    private void AppendChemicals(StringBuilder report, EntityUid target)
    {
        report.AppendLine();
        report.AppendLine(Loc.GetString("medical-records-report-chemicals-heading"));

        var any = false;
        if (TryComp<SolutionContainerManagerComponent>(target, out var manager))
        {
            foreach (var (name, solution) in _solutions.EnumerateSolutions((target, manager)))
            {
                if (name is null ||
                    name == BloodstreamComponent.DefaultBloodTemporarySolutionName ||
                    name == "print")
                    continue;

                any |= AppendSolution(report, solution.Comp.Solution);
            }
        }

        if (TryComp<BodyComponent>(target, out var body) &&
            _body.TryGetBodyOrganEntityComps<StomachComponent>((target, body), out var stomachs))
        {
            foreach (var stomach in stomachs)
            {
                if (stomach.Comp1.Solution is { } stomachSolution)
                    any |= AppendSolution(report, stomachSolution.Comp.Solution);
            }
        }

        if (!any)
            report.AppendLine(Loc.GetString("medical-records-report-none"));
    }

    private bool AppendSolution(StringBuilder report, Solution solution)
    {
        var reagents = solution.Contents.Where(reagent => reagent.Quantity > 0).ToList();
        if (reagents.Count == 0)
            return false;

        var solutionName = solution.Name != null
            ? Loc.GetString("solution-type-" + solution.Name)
            : Loc.GetString("group-solution-unknown");
        report.AppendLine(Loc.GetString("group-solution-name", ("solution", solutionName)));

        var textInfo = new CultureInfo("en-US", false).TextInfo;
        foreach (var reagent in reagents)
        {
            var reagentName = reagent.Reagent.Prototype;
            if (_prototypes.TryIndex(reagentName, out ReagentPrototype? reagentPrototype))
                reagentName = reagentPrototype.LocalizedName;

            report.AppendLine(" · " + Loc.GetString("group-solution-contents",
                ("reagent", textInfo.ToTitleCase(reagentName)),
                ("quantity", reagent.Quantity)));
        }

        return true;
    }

    private void Popup(
        Entity<HealthAnalyzerComponent> analyzer,
        EntityUid user,
        string loc,
        PopupType type = PopupType.MediumCaution)
    {
        _popup.PopupEntity(Loc.GetString(loc), analyzer, user, type);
    }
}
