// SPDX-License-Identifier: AGPL-3.0-or-later

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
using Content.Server.StationRecords.Systems;
using Content.Shared.Access;
using Content.Shared.Access.Systems;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.GameTicking;
using Content.Shared.IdentityManagement;
using Content.Shared.MedicalScanner;
using Content.Shared.MedicalRecords;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.PowerCell;
using Content.Shared.StationRecords;
using Content.Shared.Temperature.Components;
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
        if (!_access.FindAccessTags(user).Contains(MedicalAccess))
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

        if (!TryGetIdRecord(target, out var patientKey, out var patient) ||
            !TryGetIdRecord(user, out _, out var examiner))
        {
            Popup(ent, user, "health-analyzer-medical-record-missing-id");
            return;
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

        var report = BuildReport(target, patient, examiner);
        var title = Loc.GetString("medical-records-default-examination-title",
            ("time", _ticker.RoundDuration().ToString("hh\\:mm\\:ss")));

        if (!_medicalRecords.TryAddExamination(
                patientKey,
                title,
                examiner.Name,
                examiner.JobTitle,
                report))
        {
            Popup(ent, user, "health-analyzer-medical-record-save-failed");
            return;
        }

        // Rejections and errors above never consume the cooldown.
        _cooldowns[patientKey] = _timing.CurTime + ent.Comp.MedicalRecordUploadCooldown;

        var radioMessage = Loc.GetString("health-analyzer-medical-record-radio",
            ("examiner", examiner.Name),
            ("examinerJob", examiner.JobTitle),
            ("patient", patient.Name),
            ("patientJob", patient.JobTitle));
        _radio.SendRadioMessage(user, radioMessage, ent.Comp.MedicalRecordChannel, ent);

        Popup(ent, user, "health-analyzer-medical-record-saved", PopupType.Medium);
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

    private string BuildReport(EntityUid target, GeneralStationRecord patient, GeneralStationRecord examiner)
    {
        var report = new StringBuilder();
        report.AppendLine(Loc.GetString("medical-records-report-title"));
        report.AppendLine(Loc.GetString("medical-records-report-time",
            ("time", _ticker.RoundDuration().ToString("hh\\:mm\\:ss"))));
        report.AppendLine(Loc.GetString("medical-records-report-patient",
            ("name", patient.Name), ("job", patient.JobTitle)));
        report.AppendLine(Loc.GetString("medical-records-report-examiner",
            ("name", examiner.Name), ("job", examiner.JobTitle)));

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

        var mobStateId = _mobState.IsDead(target)
            ? "dead"
            : _mobState.IsCritical(target)
                ? "critical"
                : _mobState.IsAlive(target)
                    ? "alive"
                    : "invalid";
        var mobState = Loc.GetString($"medical-records-report-mob-state-{mobStateId}");
        report.AppendLine(Loc.GetString("medical-records-report-mob-state", ("state", mobState)));

        if (TryComp<TemperatureComponent>(target, out var temperature))
            report.AppendLine(Loc.GetString("medical-records-report-temperature",
                ("temperature", (temperature.CurrentTemperature - 273.15f).ToString("F1"))));

        if (TryComp<BloodstreamComponent>(target, out _))
            report.AppendLine(Loc.GetString("medical-records-report-blood",
                ("blood", (_bloodstream.GetBloodLevel(target) * 100).ToString("F1"))));

        if (TryComp<BlindableComponent>(target, out var blindable))
        {
            var weldingDamage = Math.Max(0, blindable.EyeDamage - blindable.MinDamage);
            var weldingMaximum = Math.Max(0, blindable.MaxDamage - blindable.MinDamage);
            if (weldingDamage > 0)
            {
                report.AppendLine(Loc.GetString("medical-records-report-welding-blindness",
                    ("damage", weldingDamage), ("maximum", weldingMaximum)));
            }
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

        report.AppendLine(Loc.GetString("medical-records-report-total-damage",
            ("amount", damageable.TotalDamage.ToString())));
        report.AppendLine(Loc.GetString("medical-records-report-vital-damage",
            ("amount", _threshold.CheckVitalDamage(target, damageable).ToString())));

        foreach (var (group, amount) in damageable.DamagePerGroup.OrderByDescending(x => x.Value))
        {
            if (amount <= 0)
                continue;

            report.AppendLine(Loc.GetString("medical-records-report-damage-entry",
                ("type", group), ("amount", amount.ToString())));
        }

        foreach (var (type, amount) in damageable.Damage.DamageDict.OrderByDescending(x => x.Value))
        {
            if (amount <= 0)
                continue;

            report.AppendLine(Loc.GetString("medical-records-report-damage-type-entry",
                ("type", type), ("amount", amount.ToString())));
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
        foreach (var (woundable, component) in _wound.GetAllWoundableChildren(root))
        {
            var bodyPart = _body.GetTargetBodyPart(woundable);
            var bodyPartName = Loc.GetString($"medical-records-body-part-{bodyPart}");

            if (component.Bleeds > 0)
            {
                report.AppendLine(Loc.GetString("medical-records-report-bleeding", ("part", bodyPartName)));
                any = true;
            }

            if (HasComp<IncisionOpenComponent>(woundable))
            {
                report.AppendLine(Loc.GetString("medical-records-report-open-incision", ("part", bodyPartName)));
                any = true;
            }

            if (!_trauma.TryGetWoundableTrauma(woundable, out var traumas))
                continue;

            foreach (var trauma in traumas)
            {
                report.AppendLine(Loc.GetString("medical-records-report-trauma",
                    ("part", bodyPartName),
                    ("type", trauma.Comp.TraumaType.ToString()),
                    ("severity", trauma.Comp.TraumaSeverity.ToString())));
                any = true;
            }
        }

        if (!any)
            report.AppendLine(Loc.GetString("medical-records-report-none"));
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

            report.AppendLine(Loc.GetString("medical-records-report-disease",
                ("type", disease.Genotype),
                ("progress", disease.InfectionProgress.ToString()),
                ("immunity", disease.ImmunityProgress.ToString())));
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
            report.AppendLine(Loc.GetString("medical-records-report-organ",
                ("organ", Name(organUid)),
                ("integrity", percent.ToString()),
                ("severity", organ.OrganSeverity.ToString())));
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

                any |= AppendSolution(report, name, solution.Comp.Solution);
            }
        }

        if (TryComp<BodyComponent>(target, out var body) &&
            _body.TryGetBodyOrganEntityComps<StomachComponent>((target, body), out var stomachs))
        {
            foreach (var stomach in stomachs)
            {
                if (stomach.Comp1.Solution is { } stomachSolution)
                    any |= AppendSolution(report, "stomach", stomachSolution.Comp.Solution);
            }
        }

        if (!any)
            report.AppendLine(Loc.GetString("medical-records-report-none"));
    }

    private bool AppendSolution(StringBuilder report, string name, Solution solution)
    {
        var any = false;
        foreach (var reagent in solution.Contents)
        {
            if (reagent.Quantity <= 0)
                continue;

            var reagentName = reagent.Reagent.Prototype;
            if (_prototypes.TryIndex(reagentName, out ReagentPrototype? reagentPrototype))
                reagentName = reagentPrototype.LocalizedName;

            report.AppendLine(Loc.GetString("medical-records-report-chemical",
                ("solution", name),
                ("reagent", reagentName),
                ("quantity", reagent.Quantity.ToString())));
            any = true;
        }

        return any;
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
