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
        new("case_outcomes", "Case outcomes", null,
        [
            new("recovered", "Recovered", "صحت یاب"), new("improved", "Improved", "بہتر"),
            new("referred", "Referred", "ریفر کیا گیا"), new("lost_to_follow_up", "Lost to follow-up", "رابطہ منقطع"),
            new("ongoing_elsewhere", "Ongoing elsewhere", "کہیں اور زیرِ علاج"), new("delivered", "Delivered (pregnancy)", "زچگی مکمل"),
            new("deceased", "Deceased", "وفات"),
        ]),
        new("referrers", "Referring doctors and clinics", null, []),
        new("payment_methods", "Payment methods", null,
        [
            new("cash", "Cash", "نقد"), new("card", "Debit / credit card", "کارڈ"), new("bank_transfer", "Bank transfer", "بینک ٹرانسفر"),
            new("easypaisa", "Easypaisa"), new("jazzcash", "JazzCash"), new("wallet", "Patient wallet / advance", "ایڈوانس"),
        ]),
        new("expense_categories", "Expense categories", null,
        [
            new("rent", "Rent"), new("salaries", "Salaries"), new("utilities", "Electricity, gas, water"), new("supplies", "Medical supplies"),
            new("lab_reagents", "Lab reagents"), new("maintenance", "Repairs and maintenance"), new("marketing", "Marketing"),
            new("fuel", "Fuel / generator"), new("other", "Other"),
        ]),
        new("complaint_categories", "Complaint categories", null,
        [
            new("waiting", "Waiting time"), new("staff", "Staff behaviour"), new("billing", "Billing"), new("treatment", "Treatment"),
            new("cleanliness", "Cleanliness"), new("other", "Other"),
        ]),
        new("discount_reasons", "Discount reasons", null,
        [
            new("staff", "Staff / family"), new("deserving", "Deserving patient (zakat)"), new("follow_up", "Free follow-up"),
            new("doctor_approved", "Approved by doctor"), new("other", "Other"),
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
