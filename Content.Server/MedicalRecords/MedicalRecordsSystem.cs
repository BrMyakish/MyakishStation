// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.GameTicking;
using Content.Server.StationRecords.Systems;
using Content.Shared.MedicalRecords;
using Content.Shared.StationRecords;
using Content.Shared.Traits;
using Robust.Shared.Prototypes;

namespace Content.Server.MedicalRecords;

/// <summary>
/// Owns medical record data independently from the console UI.
/// </summary>
public sealed class MedicalRecordsSystem : EntitySystem
{
    private static readonly ProtoId<TraitCategoryPrototype> DisabilitiesCategory = "Disabilities";

    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly StationRecordsSystem _records = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<AfterGeneralRecordCreatedEvent>(OnGeneralRecordCreated);
    }

    private void OnGeneralRecordCreated(AfterGeneralRecordCreatedEvent ev)
    {
        var record = new MedicalRecord();

        foreach (var traitId in ev.Profile.TraitPreferences)
        {
            if (!_prototypes.TryIndex(traitId, out TraitPrototype? trait) ||
                trait.Category != DisabilitiesCategory)
            {
                continue;
            }

            record.NegativeTraits.Add(traitId.Id);
        }

        _records.AddRecordEntry(ev.Key, record);
        _records.Synchronize(ev.Key);
    }

    public bool TryAddExamination(
        StationRecordKey key,
        string title,
        string examinerName,
        string examinerJob,
        string report)
    {
        if (!_records.TryGetRecord<MedicalRecord>(key, out var record))
            return false;

        record.Examinations.Add(new MedicalExamination
        {
            Id = record.NextExaminationId++,
            AddTime = _ticker.RoundDuration(),
            Title = title,
            ExaminerName = examinerName,
            ExaminerJob = examinerJob,
            Report = report,
        });

        _records.Synchronize(key);
        return true;
    }

    public bool TrySetNotes(StationRecordKey key, string notes)
    {
        if (!_records.TryGetRecord<MedicalRecord>(key, out var record))
            return false;

        if (record.Notes == notes)
            return false;

        record.Notes = notes;
        _records.Synchronize(key);
        return true;
    }

    public bool TrySetBodyDestroyed(StationRecordKey key, bool bodyDestroyed)
    {
        if (!_records.TryGetRecord<MedicalRecord>(key, out var record) ||
            record.BodyDestroyed == bodyDestroyed)
        {
            return false;
        }

        record.BodyDestroyed = bodyDestroyed;
        _records.Synchronize(key);
        return true;
    }

    public bool TryUpdateExamination(StationRecordKey key, uint id, string title, string note)
    {
        if (!_records.TryGetRecord<MedicalRecord>(key, out var record))
            return false;

        var examination = record.Examinations.FirstOrDefault(x => x.Id == id);
        if (examination == null)
            return false;

        examination.Title = title;
        examination.Note = note;
        _records.Synchronize(key);
        return true;
    }

    public bool TryDeleteExamination(StationRecordKey key, uint id)
    {
        if (!_records.TryGetRecord<MedicalRecord>(key, out var record))
            return false;

        var removed = record.Examinations.RemoveAll(x => x.Id == id) > 0;
        if (removed)
            _records.Synchronize(key);

        return removed;
    }
}
