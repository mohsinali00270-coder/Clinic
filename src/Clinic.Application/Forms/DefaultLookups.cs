namespace Clinic.Application.Forms;

/// <summary>System lookup lists every tenant starts with. Admins can add items; the lists cannot be deleted.</summary>
public static class DefaultLookups
{
    public sealed record Item(string Code, string En, string? Ur = null, string? Parent = null);

    public sealed record List(string Key, string Name, string? ParentListKey, IReadOnlyList<Item> Items);

    public static IReadOnlyList<List> All { get; } =
    [
        new("gender", "Gender", null,
        [
            new("M", "Male", "مرد"), new("F", "Female", "عورت"), new("O", "Other", "دیگر"),
        ]),
        new("blood_group", "Blood group", null,
        [
            new("A+", "A+"), new("A-", "A-"), new("B+", "B+"), new("B-", "B-"),
            new("AB+", "AB+"), new("AB-", "AB-"), new("O+", "O+"), new("O-", "O-"),
        ]),
        new("marital_status", "Marital status", null,
        [
            new("single", "Single", "غیر شادی شدہ"), new("married", "Married", "شادی شدہ"),
            new("divorced", "Divorced", "طلاق یافتہ"), new("widowed", "Widowed", "بیوہ / رنڈوا"),
        ]),
        new("relationship", "Relationship", null,
        [
            new("spouse", "Spouse", "شریکِ حیات"), new("parent", "Parent", "والدین"), new("child", "Child", "اولاد"),
            new("sibling", "Sibling", "بہن / بھائی"), new("other", "Other", "دیگر"),
        ]),
        new("referral_source", "Referral source", null,
        [
            new("walk_in", "Walk-in"), new("doctor", "Referred by doctor"), new("social_media", "Social media"),
            new("friend_family", "Friend or family"), new("panel", "Panel / company"), new("other", "Other"),
        ]),
        new("specialties", "Specialties", null,
        [
            new("general", "General OPD", "جنرل او پی ڈی"), new("dental", "Dental", "دانتوں کا علاج"),
            new("gynae", "Gynaecology & Obstetrics", "زچہ و بچہ"), new("paeds", "Paediatrics", "امراضِ اطفال"),
            new("physio", "Physiotherapy", "فزیو تھراپی"), new("derma", "Dermatology", "جلد"),
            new("ent", "ENT", "کان، ناک، گلا"), new("eye", "Ophthalmology", "آنکھ"),
        ]),
        new("cancel_reasons", "Cancellation reasons", null,
        [
            new("patient_request", "Patient asked to cancel", "مریض کی درخواست"), new("doctor_unavailable", "Doctor unavailable", "ڈاکٹر دستیاب نہیں"),
            new("feeling_better", "Patient feeling better"), new("travel", "Travel / transport problem"),
            new("duplicate", "Duplicate booking"), new("other", "Other", "دیگر"),
        ]),
        new("reschedule_reasons", "Reschedule reasons", null,
        [
            new("patient_request", "Patient asked to change"), new("doctor_unavailable", "Doctor unavailable"),
            new("clinic_closed", "Clinic closed"), new("other", "Other"),
        ]),
        new("cities", "Cities", null,
        [
            new("KHI", "Karachi", "کراچی"), new("LHE", "Lahore", "لاہور"), new("ISB", "Islamabad", "اسلام آباد"),
            new("RWP", "Rawalpindi", "راولپنڈی"), new("PEW", "Peshawar", "پشاور"), new("UET", "Quetta", "کوئٹہ"),
            new("MUX", "Multan", "ملتان"), new("LYP", "Faisalabad", "فیصل آباد"),
        ]),
        new("areas", "Areas", "cities",
        [
            new("khi_clifton", "Clifton", "کلفٹن", "KHI"), new("khi_dha", "DHA Karachi", null, "KHI"),
            new("khi_gulshan", "Gulshan-e-Iqbal", "گلشنِ اقبال", "KHI"), new("khi_saddar", "Saddar", "صدر", "KHI"),
            new("lhe_gulberg", "Gulberg", "گلبرگ", "LHE"), new("lhe_dha", "DHA Lahore", null, "LHE"),
            new("lhe_johar", "Johar Town", "جوہر ٹاؤن", "LHE"),
            new("isb_f6", "F-6", null, "ISB"), new("isb_g9", "G-9", null, "ISB"), new("isb_e11", "E-11", null, "ISB"),
        ]),
    ];
}
