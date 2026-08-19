// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Access.Systems;
using Content.Server.Popups;
using Content.Server.Radio.EntitySystems;
using Content.Server.Station.Systems;
using Content.Server.StationRecords.Components;
using Content.Server.StationRecords.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.MedicalRecords;
using Content.Shared.MedicalRecords.Components;
using Content.Shared.Popups;
using Content.Shared.StationRecords;
using Robust.Server.GameObjects;

namespace Content.Server.MedicalRecords;

/// <summary>
/// Medical records are public to read, while every mutation is validated against Medical access.
/// </summary>
public sealed class MedicalRecordsConsoleSystem : EntitySystem
{
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly IdCardSystem _idCard = default!;
    [Dependency] private readonly IdExaminableSystem _idExaminable = default!;
    [Dependency] private readonly MedicalRecordsSystem _medicalRecords = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly StationRecordsSystem _records = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<MedicalRecordsConsoleComponent, RecordModifiedEvent>(OnRecordChanged);
        SubscribeLocalEvent<MedicalRecordsConsoleComponent, AfterGeneralRecordCreatedEvent>(OnRecordChanged);

        Subs.BuiEvents<MedicalRecordsConsoleComponent>(MedicalRecordsConsoleKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<SelectStationRecord>(OnKeySelected);
            subs.Event<SetStationRecordFilter>(OnFilterChanged);
            subs.Event<MedicalRecordSetCategoryFilterMessage>(OnCategoryFilterChanged);
            subs.Event<MedicalRecordSetNotesMessage>(OnSetNotes);
            subs.Event<MedicalRecordSetBodyDestroyedMessage>(OnSetBodyDestroyed);
            subs.Event<MedicalRecordUpdateExaminationMessage>(OnUpdateExamination);
            subs.Event<MedicalRecordDeleteExaminationMessage>(OnDeleteExamination);
        });

        Subs.BuiEvents<MedicalNotesComponent>(MedicalNotesUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnRemoteUiOpened);
            subs.Event<MedicalNotesSetMessage>(OnRemoteSetNotes);
        });
    }

    private void OnRecordChanged<T>(Entity<MedicalRecordsConsoleComponent> ent, ref T args)
    {
        UpdateUi(ent);
    }

    private void OnUiOpened(Entity<MedicalRecordsConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent);
    }

    private void OnRemoteUiOpened(Entity<MedicalNotesComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (!CanUseRemoteNotes(ent, args.Actor))
            return;

        UpdateRemoteUi(ent);
    }

    private void OnKeySelected(Entity<MedicalRecordsConsoleComponent> ent, ref SelectStationRecord msg)
    {
        ent.Comp.ActiveKey = msg.SelectedKey;
        UpdateUi(ent);
    }

    private void OnFilterChanged(Entity<MedicalRecordsConsoleComponent> ent, ref SetStationRecordFilter msg)
    {
        if (msg.Type is StationRecordFilterType.DNA or StationRecordFilterType.Prints)
            return;

        if (ent.Comp.Filter?.Type == msg.Type && ent.Comp.Filter.Value == msg.Value)
            return;

        ent.Comp.Filter = new StationRecordsFilter(msg.Type, msg.Value);
        UpdateUi(ent);
    }

    private void OnCategoryFilterChanged(
        Entity<MedicalRecordsConsoleComponent> ent,
        ref MedicalRecordSetCategoryFilterMessage msg)
    {
        if (!Enum.IsDefined(msg.Filter) || ent.Comp.CategoryFilter == msg.Filter)
            return;

        ent.Comp.CategoryFilter = msg.Filter;
        UpdateUi(ent);
    }

    private void OnSetNotes(Entity<MedicalRecordsConsoleComponent> ent, ref MedicalRecordSetNotesMessage msg)
    {
        if (!TryGetEditableKey(ent, msg.Actor, out var key))
            return;

        var notes = msg.Notes.Trim();
        if (notes.Length > ent.Comp.MaxNoteLength)
            return;

        if (_medicalRecords.TrySetNotes(key, notes))
            UpdateUi(ent);
    }

    private void OnSetBodyDestroyed(
        Entity<MedicalRecordsConsoleComponent> ent,
        ref MedicalRecordSetBodyDestroyedMessage msg)
    {
        if (!TryGetEditableKey(ent, msg.Actor, out var key) ||
            !_medicalRecords.TrySetBodyDestroyed(key, msg.BodyDestroyed))
        {
            return;
        }

        UpdateUi(ent);
        NotifyBodyDestroyedChanged(key, msg.Actor, msg.BodyDestroyed, ent);
    }

    private void OnUpdateExamination(Entity<MedicalRecordsConsoleComponent> ent, ref MedicalRecordUpdateExaminationMessage msg)
    {
        if (!TryGetEditableKey(ent, msg.Actor, out var key))
            return;

        var title = msg.Title.Trim();
        var note = msg.Note.Trim();
        if (title.Length < 1 || title.Length > ent.Comp.MaxTitleLength || note.Length > ent.Comp.MaxNoteLength)
            return;

        if (_medicalRecords.TryUpdateExamination(key, msg.Id, title, note))
            UpdateUi(ent);
    }

    private void OnDeleteExamination(Entity<MedicalRecordsConsoleComponent> ent, ref MedicalRecordDeleteExaminationMessage msg)
    {
        if (!TryGetEditableKey(ent, msg.Actor, out var key))
            return;

        var examinationId = msg.Id;
        if (!_records.TryGetRecord<MedicalRecord>(key, out var medicalRecord) ||
            medicalRecord.Examinations.FirstOrDefault(x => x.Id == examinationId) is not { } examination)
        {
            return;
        }

        var examinationTitle = examination.Title;
        if (_medicalRecords.TryDeleteExamination(key, examinationId))
        {
            UpdateUi(ent);
            NotifyExaminationDeleted(key, msg.Actor, examinationTitle, ent);
        }
    }

    private void OnRemoteSetNotes(Entity<MedicalNotesComponent> ent, ref MedicalNotesSetMessage msg)
    {
        if (!CanUseRemoteNotes(ent, msg.Actor) ||
            !TryGetTargetRecord(ent, out var key, out _, out _))
        {
            return;
        }

        var notes = msg.Notes.Trim();
        if (notes.Length > MedicalRecordsConsoleComponent.DefaultMaxNoteLength)
            return;

        if (_medicalRecords.TrySetNotes(key, notes))
            UpdateRemoteUi(ent);
    }

    private bool CanUseRemoteNotes(Entity<MedicalNotesComponent> target, EntityUid user)
    {
        if (_idExaminable.CanAccessMedicalNotes(user))
            return true;

        _popup.PopupEntity(
            Loc.GetString("medical-notes-access-denied"),
            target,
            user,
            PopupType.MediumCaution);
        _ui.CloseUi(target.Owner, MedicalNotesUiKey.Key, user);
        return false;
    }

    private bool TryGetEditableKey(
        Entity<MedicalRecordsConsoleComponent> ent,
        EntityUid user,
        out StationRecordKey key)
    {
        key = default;

        if (!_access.IsAllowed(user, ent))
        {
            _popup.PopupEntity(Loc.GetString("medical-records-permission-denied"), ent, user, PopupType.MediumCaution);
            return false;
        }

        if (ent.Comp.ActiveKey is not { } id || _station.GetOwningStation(ent) is not { } station)
            return false;

        key = new StationRecordKey(id, station);
        return true;
    }

    private void UpdateUi(Entity<MedicalRecordsConsoleComponent> ent)
    {
        var station = _station.GetOwningStation(ent);
        if (!TryComp<StationRecordsComponent>(station, out var stationRecords))
        {
            _ui.SetUiState(ent.Owner, MedicalRecordsConsoleKey.Key, new MedicalRecordsConsoleState());
            return;
        }

        var listing = _records.BuildListing((station.Value, stationRecords), ent.Comp.Filter);
        FilterByMedicalCategory(listing, station.Value, stationRecords, ent.Comp.CategoryFilter);
        if (ent.Comp.ActiveKey is { } active && !listing.ContainsKey(active))
            ent.Comp.ActiveKey = null;

        MedicalRecordProfile? profile = null;
        MedicalRecord? medicalRecord = null;

        if (ent.Comp.ActiveKey is { } id)
        {
            var key = new StationRecordKey(id, station.Value);
            if (_records.TryGetRecord<GeneralStationRecord>(key, out var general, stationRecords))
                profile = new MedicalRecordProfile(general.Name, general.JobTitle, general.Species, general.Age);

            _records.TryGetRecord(key, out medicalRecord, stationRecords);
        }

        var state = new MedicalRecordsConsoleState(
            ent.Comp.ActiveKey,
            profile,
            medicalRecord,
            listing,
            ent.Comp.Filter,
            ent.Comp.CategoryFilter);

        _ui.SetUiState(ent.Owner, MedicalRecordsConsoleKey.Key, state);
    }

    private void UpdateRemoteUi(Entity<MedicalNotesComponent> ent)
    {
        if (!TryGetTargetRecord(ent, out _, out var generalRecord, out var medicalRecord))
        {
            _ui.CloseUi(ent.Owner, MedicalNotesUiKey.Key);
            return;
        }

        _ui.SetUiState(
            ent.Owner,
            MedicalNotesUiKey.Key,
            new MedicalNotesState(generalRecord.Name, generalRecord.JobTitle, medicalRecord.Notes));
    }

    private bool TryGetTargetRecord(
        EntityUid target,
        out StationRecordKey key,
        out GeneralStationRecord generalRecord,
        out MedicalRecord medicalRecord)
    {
        key = default;
        generalRecord = default!;
        medicalRecord = default!;

        if (_station.GetOwningStation(target) is not { } station)
            return false;

        var targetName = MetaData(target).EntityName;
        if (_records.GetRecordByName(station, targetName) is not { } id)
            return false;

        key = new StationRecordKey(id, station);
        if (!_records.TryGetRecord(key, out GeneralStationRecord? foundGeneralRecord))
            return false;

        if (!_records.TryGetRecord(key, out MedicalRecord? foundMedicalRecord))
            return false;

        generalRecord = foundGeneralRecord;
        medicalRecord = foundMedicalRecord;
        return true;
    }

    private void FilterByMedicalCategory(
        Dictionary<uint, string> listing,
        EntityUid station,
        StationRecordsComponent stationRecords,
        MedicalRecordCategoryFilter filter)
    {
        if (filter == MedicalRecordCategoryFilter.All)
            return;

        foreach (var id in listing.Keys.ToArray())
        {
            var key = new StationRecordKey(id, station);
            if (!_records.TryGetRecord<MedicalRecord>(key, out var record, stationRecords) ||
                !MatchesMedicalCategory(record, filter))
            {
                listing.Remove(id);
            }
        }
    }

    private static bool MatchesMedicalCategory(MedicalRecord record, MedicalRecordCategoryFilter filter)
    {
        return filter switch
        {
            MedicalRecordCategoryFilter.NoExaminations => !record.BodyDestroyed && record.Examinations.Count == 0,
            MedicalRecordCategoryFilter.HasExaminations => !record.BodyDestroyed && record.Examinations.Count > 0,
            MedicalRecordCategoryFilter.BodyDestroyed => record.BodyDestroyed,
            _ => true,
        };
    }

    private void NotifyBodyDestroyedChanged(
        StationRecordKey key,
        EntityUid editor,
        bool bodyDestroyed,
        Entity<MedicalRecordsConsoleComponent> console)
    {
        if (!_records.TryGetRecord<GeneralStationRecord>(key, out var patient))
            return;

        GetMedicalWorker(editor, out var editorName, out var editorJob);
        var message = Loc.GetString(
            bodyDestroyed
                ? "medical-records-radio-body-destroyed"
                : "medical-records-radio-body-destroyed-cleared",
            ("editor", editorName),
            ("editorJob", editorJob),
            ("patient", patient.Name),
            ("patientJob", patient.JobTitle));
        _radio.SendRadioMessage(console.Owner, message, console.Comp.MedicalChannel, console.Owner);
    }

    private void NotifyExaminationDeleted(
        StationRecordKey key,
        EntityUid editor,
        string examinationTitle,
        Entity<MedicalRecordsConsoleComponent> console)
    {
        if (!_records.TryGetRecord<GeneralStationRecord>(key, out var patient))
            return;

        GetMedicalWorker(editor, out var editorName, out var editorJob);
        var message = Loc.GetString("medical-records-radio-examination-deleted",
            ("editor", editorName),
            ("editorJob", editorJob),
            ("title", examinationTitle),
            ("patient", patient.Name),
            ("patientJob", patient.JobTitle));
        _radio.SendRadioMessage(console.Owner, message, console.Comp.MedicalChannel, console.Owner);
    }

    private void GetMedicalWorker(EntityUid user, out string name, out string job)
    {
        name = Identity.Name(user, EntityManager);
        job = Loc.GetString("medical-records-report-unknown");

        if (!_idCard.TryFindIdCard(user, out var idCard))
            return;

        if (idCard.Comp.FullName is { } fullName && !string.IsNullOrWhiteSpace(fullName))
            name = fullName;
        if (idCard.Comp.LocalizedJobTitle is { } jobTitle && !string.IsNullOrWhiteSpace(jobTitle))
            job = jobTitle;
    }

}
