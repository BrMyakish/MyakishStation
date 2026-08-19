// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.StationRecords;
using Robust.Shared.Serialization;

namespace Content.Shared.MedicalRecords;

[Serializable, NetSerializable]
public enum MedicalRecordsConsoleKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum MedicalNotesUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum MedicalRecordCategoryFilter : byte
{
    All,
    NoExaminations,
    HasExaminations,
    BodyDestroyed,
}

/// <summary>
/// Only the public demographic subset needed by the medical console is sent to viewers.
/// </summary>
[Serializable, NetSerializable]
public sealed record MedicalRecordProfile(string Name, string Job, string Species, int Age);

[Serializable, NetSerializable]
public sealed class MedicalRecordsConsoleState : BoundUserInterfaceState
{
    public readonly uint? SelectedKey;
    public readonly MedicalRecordProfile? Profile;
    public readonly MedicalRecord? MedicalRecord;
    public readonly Dictionary<uint, string>? RecordListing;
    public readonly StationRecordsFilter? Filter;
    public readonly MedicalRecordCategoryFilter CategoryFilter;
    public readonly bool CanEdit;

    public MedicalRecordsConsoleState(
        uint? selectedKey = null,
        MedicalRecordProfile? profile = null,
        MedicalRecord? medicalRecord = null,
        Dictionary<uint, string>? recordListing = null,
        StationRecordsFilter? filter = null,
        MedicalRecordCategoryFilter categoryFilter = MedicalRecordCategoryFilter.All,
        bool canEdit = false)
    {
        SelectedKey = selectedKey;
        Profile = profile;
        MedicalRecord = medicalRecord;
        RecordListing = recordListing;
        Filter = filter;
        CategoryFilter = categoryFilter;
        CanEdit = canEdit;
    }
}

[Serializable, NetSerializable]
public sealed class MedicalRecordSetCategoryFilterMessage(MedicalRecordCategoryFilter filter) : BoundUserInterfaceMessage
{
    public readonly MedicalRecordCategoryFilter Filter = filter;
}

[Serializable, NetSerializable]
public sealed class MedicalRecordSetBodyDestroyedMessage(bool bodyDestroyed) : BoundUserInterfaceMessage
{
    public readonly bool BodyDestroyed = bodyDestroyed;
}

[Serializable, NetSerializable]
public sealed class MedicalRecordSetNotesMessage(string notes) : BoundUserInterfaceMessage
{
    public readonly string Notes = notes;
}

[Serializable, NetSerializable]
public sealed class MedicalRecordUpdateExaminationMessage(uint id, string title, string note) : BoundUserInterfaceMessage
{
    public readonly uint Id = id;
    public readonly string Title = title;
    public readonly string Note = note;
}

[Serializable, NetSerializable]
public sealed class MedicalRecordDeleteExaminationMessage(uint id) : BoundUserInterfaceMessage
{
    public readonly uint Id = id;
}

[Serializable, NetSerializable]
public sealed class MedicalNotesState(string patientName, string patientJob, string notes) : BoundUserInterfaceState
{
    public readonly string PatientName = patientName;
    public readonly string PatientJob = patientJob;
    public readonly string Notes = notes;
}

[Serializable, NetSerializable]
public sealed class MedicalNotesSetMessage(string notes) : BoundUserInterfaceMessage
{
    public readonly string Notes = notes;
}
