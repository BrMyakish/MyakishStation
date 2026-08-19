// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared.MedicalRecords;

/// <summary>
/// Medical information attached to a general station record.
/// </summary>
[Serializable, NetSerializable, DataRecord]
public sealed partial record MedicalRecord
{
    /// <summary>
    /// Permanent negative character traits. Dynamic scanner findings do not belong here.
    /// </summary>
    [DataField]
    public List<string> NegativeTraits = new();

    /// <summary>
    /// Free-form notes shared by medical personnel.
    /// </summary>
    [DataField]
    public string Notes = string.Empty;

    /// <summary>
    /// Manually set when the patient's former body has been irreversibly destroyed.
    /// This is independent from the mob's current health state.
    /// </summary>
    [DataField]
    public bool BodyDestroyed;

    /// <summary>
    /// Health analyzer reports saved during the round.
    /// </summary>
    [DataField]
    public List<MedicalExamination> Examinations = new();

    [DataField]
    public uint NextExaminationId;
}

[Serializable, NetSerializable, DataRecord]
public sealed partial record MedicalExamination
{
    [DataField]
    public uint Id;

    [DataField]
    public TimeSpan AddTime;

    [DataField]
    public string Title = string.Empty;

    [DataField]
    public string ExaminerName = string.Empty;

    [DataField]
    public string ExaminerJob = string.Empty;

    [DataField]
    public string Report = string.Empty;

    [DataField]
    public string Note = string.Empty;
}
