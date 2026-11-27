namespace Clinic.Shared.Sync;

/// <summary>
/// camelCase fields a device may write for the Phase 5b entities. Shared by the server's SyncRegistry and the
/// device's LocalSyncRegistry so the two lists cannot drift apart.
/// </summary>
public static class CareSyncFields
{
    public static readonly IReadOnlySet<string> Case = new HashSet<string>
    {
        "patientId", "branchId", "caseTypeId", "caseTypeCode", "title", "primaryDoctorId", "openedAt", "status", "plannedSessions",
        "followUpIntervalDays", "closedAt", "closedBy", "outcome", "closingSummary", "patientGender", "patientDateOfBirth", "formVersionId",
    };

    public static readonly IReadOnlySet<string> CarePlanStep = new HashSet<string>
    {
        "caseId", "sortOrder", "kind", "title", "dueDate", "status", "completedAt", "completedBy", "linkedRecordId", "notes",
    };

    public static readonly IReadOnlySet<string> CaseMember = new HashSet<string> { "caseId", "doctorId", "userId", "name", "role" };

    public static readonly IReadOnlySet<string> CaseNote = new HashSet<string> { "caseId", "body", "writtenAt", "writtenBy", "authorName" };

    public static readonly IReadOnlySet<string> Referral = new HashSet<string>
    {
        "patientId", "branchId", "direction", "encounterId", "caseId", "fromDoctorId", "toDoctorId", "toSpecialty", "externalFacility",
        "externalDoctor", "sourceCode", "sourceName", "reason", "priority", "status", "referredAt", "referredBy", "respondedAt",
        "respondedBy", "responseNote", "summary",
    };

    public static readonly IReadOnlySet<string> Procedure = new HashSet<string>
    {
        "patientId", "branchId", "procedureTypeId", "procedureCode", "name", "category", "fee", "consumablesJson", "encounterId", "caseId",
        "admissionId", "doctorId", "performedBy", "plannedAt", "startedAt", "completedAt", "status", "cancelReason", "consentRequired",
        "consentSignedAt", "consentSignerName", "consentRelation", "consentWitness", "consentAttachmentId", "patientGender",
        "patientDateOfBirth", "formVersionId",
    };

    public static readonly IReadOnlySet<string> Admission = new HashSet<string>
    {
        "patientId", "branchId", "bedId", "caseId", "doctorId", "admittedAt", "admittedBy", "reason", "status", "dischargedAt",
        "dischargedBy", "dischargeSummary", "dischargeAdvice",
    };

    public static readonly IReadOnlySet<string> BedTransfer = new HashSet<string> { "admissionId", "fromBedId", "toBedId", "movedAt", "movedBy", "reason" };

    public static readonly IReadOnlySet<string> NursingNote = new HashSet<string> { "admissionId", "kind", "body", "writtenAt", "writtenBy", "authorName" };

    public static readonly IReadOnlySet<string> MedicationOrder = new HashSet<string>
    {
        "admissionId", "drugId", "drugName", "dose", "route", "frequency", "startAt", "stopAt", "status", "instructions", "orderedBy",
    };

    public static readonly IReadOnlySet<string> MedicationDose = new HashSet<string> { "orderId", "admissionId", "givenAt", "status", "note", "givenBy" };

    // Phase 6 ---------------------------------------------------------------------------------------

    public static readonly IReadOnlySet<string> LabOrder = new HashSet<string>
    {
        "patientId", "branchId", "encounterId", "caseId", "doctorId", "orderedAt", "orderedBy", "priority", "clinicalNotes", "status",
    };

    public static readonly IReadOnlySet<string> LabResult = new HashSet<string>
    {
        "orderId", "labTestId", "testCode", "testName", "sampleType", "fee", "status", "sampleNo", "collectedAt", "collectedBy",
        "resultedAt", "resultedBy", "verifiedAt", "verifiedBy", "comment", "isAbnormal", "cancelReason", "patientGender",
        "patientDateOfBirth", "formVersionId",
    };

    public static readonly IReadOnlySet<string> Invoice = new HashSet<string>
    {
        "patientId", "branchId", "invoiceNo", "issuedAt", "status", "encounterId", "caseId", "admissionId", "subtotal", "discount",
        "discountReason", "tax", "total", "notes", "voidReason", "payerId", "panelId", "payerAmount", "patientAmount",
    };

    public static readonly IReadOnlySet<string> InvoiceLine = new HashSet<string>
    {
        "invoiceId", "source", "sourceId", "kind", "description", "quantity", "unitPrice", "discount", "taxPercent", "amount", "doctorId", "sortOrder",
    };

    public static readonly IReadOnlySet<string> Payment = new HashSet<string>
    {
        "patientId", "branchId", "invoiceId", "kind", "method", "amount", "receivedAt", "receivedBy", "reference", "receiptNo", "reason",
    };

    public static readonly IReadOnlySet<string> CashClosing = new HashSet<string>
    {
        "branchId", "userId", "businessDate", "closedAt", "expectedCash", "countedCash", "totalsJson", "paymentCount", "notes",
    };

    // Phase 6b --------------------------------------------------------------------------------------

    public static readonly IReadOnlySet<string> PatientPackage = new HashSet<string>
    {
        "patientId", "branchId", "packagePlanId", "name", "price", "itemsJson", "purchasedAt", "expiresAt", "caseId", "status",
    };

    public static readonly IReadOnlySet<string> PackageUsage = new HashSet<string>
    {
        "patientPackageId", "kind", "code", "source", "sourceId", "usedAt", "usedBy",
    };

    public static readonly IReadOnlySet<string> PatientPanel = new HashSet<string>
    {
        "patientId", "payerId", "memberNo", "employer", "relation", "coPayPercent", "validFrom", "validTo", "isActive",
    };

    public static readonly IReadOnlySet<string> CreditNote = new HashSet<string>
    {
        "invoiceId", "kind", "amount", "reason", "status", "requestedAt", "requestedBy", "decidedAt", "decidedBy", "decisionNote",
    };

    public static readonly IReadOnlySet<string> Expense = new HashSet<string>
    {
        "branchId", "category", "amount", "spentOn", "method", "paidTo", "notes", "recordedBy",
    };

    public static readonly IReadOnlySet<string> ContactPreference = new HashSet<string> { "patientId", "language", "channel", "optOut" };

    public static readonly IReadOnlySet<string> OutboundMessage = new HashSet<string>
    {
        "patientId", "branchId", "channel", "templateKey", "to", "body", "language", "queuedAt", "queuedBy", "relatedId",
    };

    public static readonly IReadOnlySet<string> Feedback = new HashSet<string>
    {
        "patientId", "branchId", "encounterId", "doctorId", "overallRating", "doctorRating", "serviceRating", "comment", "submittedAt",
        "channel", "formVersionId",
    };

    public static readonly IReadOnlySet<string> Complaint = new HashSet<string>
    {
        "patientId", "branchId", "subject", "details", "category", "status", "raisedAt", "raisedBy", "assignedTo", "assignedName",
        "resolution", "resolvedAt", "resolvedBy",
    };

    // Phase 7 ---------------------------------------------------------------------------------------

    public static readonly IReadOnlySet<string> StockBatch = new HashSet<string>
    {
        "itemId", "branchId", "batchNo", "expiry", "unitCost", "supplier", "receivedAt",
    };

    public static readonly IReadOnlySet<string> StockMovement = new HashSet<string>
    {
        "itemId", "batchId", "branchId", "kind", "quantity", "movedAt", "movedBy", "reference", "reason", "sourceId",
    };

    public static readonly IReadOnlySet<string> Dispensation = new HashSet<string>
    {
        "patientId", "branchId", "itemId", "itemName", "prescriptionItemId", "quantity", "unitPrice", "dispensedAt", "dispensedBy", "notes",
    };

    public static readonly IReadOnlySet<string> AccessEvent = new HashSet<string> { "branchId", "entityType", "recordId", "action", "at", "userId" };
}
