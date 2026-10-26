import AppKit
import SwiftUI
import ServiceManagement

// MARK: - Κατάσταση εφαρμογής

/// Ό,τι δείχνει και αλλάζει το παράθυρο. Το λεξιλόγιο και το ιστορικό αποθηκεύονται μόλις αλλάξουν.
final class AppModel: ObservableObject {
    enum Phase { case idle, recording, transcribing }

    @Published var phase: Phase = .idle
    @Published var shortcutsActive = false
    @Published var accessibilityGranted = false
    @Published var microphoneGranted = false
    @Published var hasElevenLabsKey = Settings.shared.hasElevenLabsKey
    @Published var lastError = ""
    @Published var inputDevices: [AudioInputDevice] = []
    @Published var launchAtLogin = SMAppService.mainApp.status == .enabled

    @Published var speechModel = Settings.shared.model {
        didSet { Settings.shared.model = speechModel }
    }
    @Published var microphoneUID = Settings.shared.microphoneUID {
        didSet { Settings.shared.microphoneUID = microphoneUID }
    }
    @Published var soundsEnabled = Settings.shared.soundsEnabled {
        didSet { Settings.shared.soundsEnabled = soundsEnabled }
    }
    @Published var pushToTalk = Settings.shared.pushToTalk {
        didSet { Settings.shared.pushToTalk = pushToTalk; shortcutsChanged() }
    }
    @Published var handsFree = Settings.shared.handsFree {
        didSet { Settings.shared.handsFree = handsFree; shortcutsChanged() }
    }
    @Published var dailyGoal = Settings.shared.dailyGoal {
        didSet { Settings.shared.dailyGoal = dailyGoal }
    }
    @Published var vocabulary = Vocabulary.load() {
        didSet { try? vocabulary.save() }
    }
    @Published var history = HistoryStore.load() {
        didSet { HistoryStore.save(history) }
    }

    var importFromWispr: () -> Void = {}
    var openAccessibility: () -> Void = {}
    var resetAccessibility: () -> Void = {}
    var openMicrophone: () -> Void = {}
    var openLog: () -> Void = {}
    var shortcutsChanged: () -> Void = {}
    /// Καταγραφή νέας συντόμευσης από τα πλήκτρα (το συνδέει ο AppDelegate με την παρακολούθηση πλήκτρων).
    var captureShortcut: (@escaping (Combo?) -> Void) -> Void = { $0(nil) }
    var cancelCapture: () -> Void = {}
    @Published var capturing: String?        // "ptt" ή "hands" όσο καταγράφεται νέα συντόμευση
    @Published var shortcutError = ""

    /// Η συντόμευση «κράτα και μίλα» δεν μένει ποτέ κενή: χωρίς αυτήν δεν υπαγορεύεις.
    var pushToTalkCombo: Combo {
        let combo = Combo.parse(pushToTalk)
        return combo.isEmpty ? Combo.parse(Combo.defaultPushToTalk) : combo
    }
    var handsFreeCombo: Combo { Combo.parse(handsFree) }

    func startCapture(_ which: String) {
        if capturing != nil { cancelCapture() }
        capturing = which
        shortcutError = ""
        captureShortcut { [weak self] combo in
            guard let self, self.capturing == which else { return }
            self.capturing = nil
            if let combo { self.setShortcut(which, combo) }
        }
    }

    func stopCapture() {
        cancelCapture()
        capturing = nil
    }

    func setShortcut(_ which: String, _ combo: Combo) {
        let other = which == "ptt" ? handsFreeCombo : pushToTalkCombo
        shortcutError = ""
        if !combo.isEmpty && !combo.isSafe {
            shortcutError = "Το «\(combo.label)» το πατάς συχνά όταν γράφεις. Διάλεξε συνδυασμό με Control, Option, Command ή Fn, ένα πλήκτρο F ή κουμπί του ποντικιού."
        } else if !combo.isEmpty && combo == other {
            shortcutError = "Το «\(combo.label)» είναι ήδη η άλλη συντόμευση."
        } else if which == "ptt" {
            pushToTalk = combo.string
        } else {
            handsFree = combo.string
        }
    }

    var stats: DictationStats { DictationStats(history) }

    func refreshDevices() { inputDevices = AudioDevices.inputs() }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            lastError = "Άνοιγμα με την εκκίνηση: \(error.localizedDescription)"
        }
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }
}

// MARK: - Εμφάνιση

/// Δική μας παλέτα: σκούρο μπλε πλαϊνό, ψυχρό ανοιχτό γκρι περιεχόμενο. Χρώματα Okabe-Ito
/// (μπλε, γαλάζιο, πορτοκαλί), που ξεχωρίζουν και με χρωματοψία· κανένα σήμα δεν βασίζεται μόνο στο χρώμα.
enum Theme {
    static let sidebar = Color(red: 0.06, green: 0.10, blue: 0.19)
    static let sidebarText = Color.white
    static let sidebarMuted = Color.white.opacity(0.62)
    static let sidebarSelection = Color.white.opacity(0.12)
    static let background = Color(red: 0.953, green: 0.961, blue: 0.976)
    static let card = Color.white
    static let border = Color(red: 0.06, green: 0.10, blue: 0.19).opacity(0.10)
    static let text = Color(red: 0.08, green: 0.11, blue: 0.18)
    static let secondary = Color(red: 0.36, green: 0.40, blue: 0.48)
    static let blue = Color(red: 0, green: 0.447, blue: 0.698)
    static let sky = Color(red: 0.337, green: 0.706, blue: 0.914)
    static let orange = Color(red: 0.835, green: 0.369, blue: 0)
    static let amber = Color(red: 0.902, green: 0.624, blue: 0)
    static let chip = Color(red: 0.0, green: 0.447, blue: 0.698).opacity(0.08)

    /// Η γραμματοσειρά του Mac (SF Pro) με όλα τα βάρη της, αντί για Open Sans: οι τίτλοι, τα κείμενα
    /// και οι ετικέτες ξεχωρίζουν με το βάρος και το μέγεθος, όπως στις εφαρμογές της Apple.
    static func font(_ size: CGFloat, bold: Bool = false) -> Font {
        .system(size: size, weight: bold ? .semibold : .regular)
    }

    /// Μεγάλα νούμερα: στρογγυλεμένη εκδοχή, ψηφία ίσου πλάτους ώστε να μη «χορεύουν» όταν αλλάζουν.
    static func number(_ size: CGFloat) -> Font {
        .system(size: size, weight: .semibold, design: .rounded).monospacedDigit()
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        content
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Theme.card)
            .clipShape(RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.border))
    }
}

struct KeyCap: View {
    let label: String
    var body: some View {
        Text(label)
            .font(Theme.font(12, bold: true))
            .foregroundStyle(Theme.text)
            .padding(.horizontal, 8).padding(.vertical, 4)
            .background(Theme.background)
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .overlay(RoundedRectangle(cornerRadius: 6).stroke(Theme.text.opacity(0.18)))
    }
}

struct SectionTitle: View {
    let title: String
    let subtitle: String?
    init(_ title: String, _ subtitle: String? = nil) { self.title = title; self.subtitle = subtitle }
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 28, weight: .bold)).tracking(-0.5).foregroundStyle(Theme.text)
            if let subtitle {
                Text(subtitle).font(Theme.font(13)).foregroundStyle(Theme.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

struct FieldLabel: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text.uppercased(with: Locale(identifier: "el_GR")))
            .font(.system(size: 10.5, weight: .semibold)).tracking(0.8).foregroundStyle(Theme.secondary)
    }
}

// MARK: - Παράθυρο

enum Page: String, CaseIterable, Identifiable {
    case home, dictionary, snippets, settings
    var id: String { rawValue }
    var title: String {
        switch self {
        case .home: return "Αρχική"
        case .dictionary: return "Λεξιλόγιο"
        case .snippets: return "Έτοιμα κείμενα"
        case .settings: return "Ρυθμίσεις"
        }
    }
    var icon: String {
        switch self {
        case .home: return "house"
        case .dictionary: return "character.book.closed"
        case .snippets: return "text.bubble"
        case .settings: return "gearshape"
        }
    }
}

struct RootView: View {
    @ObservedObject var model: AppModel
    // Η αρχική σελίδα μπορεί να οριστεί με `-initialPage settings` στην εκκίνηση (για στιγμιότυπα ελέγχου).
    @State private var page = Page(rawValue: UserDefaults.standard.string(forKey: "initialPage") ?? "") ?? .home

    var body: some View {
        HStack(spacing: 0) {
            Sidebar(model: model, page: $page)
                .frame(width: 224)
                .background(Theme.sidebar)
            ScrollView {
                Group {
                    switch page {
                    case .home: HomeView(model: model)
                    case .dictionary: DictionaryView(model: model)
                    case .snippets: SnippetsView(model: model)
                    case .settings: SettingsView(model: model)
                    }
                }
                .padding(.horizontal, 36).padding(.top, 44).padding(.bottom, 36)
                .frame(maxWidth: 860, alignment: .leading)
                .frame(maxWidth: .infinity)
            }
            // Κάθε σελίδα ανοίγει από την κορυφή, όχι στο σημείο που είχες κυλήσει την προηγούμενη.
            .id(page)
            .background(Theme.background)
        }
        .frame(minWidth: 960, minHeight: 660)
        .preferredColorScheme(.light)
    }
}

struct Sidebar: View {
    @ObservedObject var model: AppModel
    @Binding var page: Page

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 10) {
                if let icon = NSApp.applicationIconImage {
                    Image(nsImage: icon).resizable().frame(width: 30, height: 30)
                }
                Text(AppInfo.name).font(.system(size: 16, weight: .bold)).tracking(-0.2)
            }
            .foregroundStyle(Theme.sidebarText)
            .padding(.horizontal, 12).padding(.top, 46).padding(.bottom, 22)

            ForEach(Page.allCases) { item in
                Button { page = item } label: {
                    HStack(spacing: 10) {
                        Image(systemName: item.icon).frame(width: 20)
                        Text(item.title).font(.system(size: 13, weight: page == item ? .semibold : .medium))
                        Spacer()
                    }
                    .foregroundStyle(page == item ? Theme.sidebarText : Theme.sidebarMuted)
                    .padding(.horizontal, 12).padding(.vertical, 9)
                    .background(page == item ? Theme.sidebarSelection : .clear)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }

            Spacer()
            StatusPill(model: model)
                .padding(.bottom, 16)
        }
        .padding(.horizontal, 12)
    }
}

struct StatusPill: View {
    @ObservedObject var model: AppModel

    var body: some View {
        let ready = model.shortcutsActive && model.microphoneGranted
        let (icon, title, color): (String, String, Color) = {
            switch model.phase {
            case .recording: return ("record.circle", "Ηχογραφεί…", Theme.amber)
            case .transcribing: return ("ellipsis.circle", "Γράφει…", Theme.sky)
            case .idle: return ready ? ("checkmark.circle.fill", "Έτοιμο", Theme.sky)
                                     : ("exclamationmark.triangle.fill", "Λείπει άδεια", Theme.amber)
            }
        }()
        return VStack(alignment: .leading, spacing: 6) {
            Label(title, systemImage: icon).font(Theme.font(13, bold: true)).foregroundStyle(color)
            Text(model.speechModel == .scribe ? "Scribe v2 · ElevenLabs" : "Whisper · τοπικά")
                .font(Theme.font(12)).foregroundStyle(Theme.sidebarMuted)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.white.opacity(0.07))
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.white.opacity(0.12)))
    }
}

// MARK: - Αρχική

struct HomeView: View {
    @ObservedObject var model: AppModel
    @State private var search = ""

    var body: some View {
        let stats = model.stats
        VStack(alignment: .leading, spacing: 24) {
            SectionTitle("Καλώς ήρθες")

            if !model.shortcutsActive || !model.microphoneGranted {
                PermissionBanner(model: model)
            }

            HabitTracker(stats: stats, goal: model.dailyGoal)

            Card {
                HStack(spacing: 8) {
                    Text("Κράτα").font(Theme.font(13))
                    let keys = model.pushToTalkCombo.keys
                    ForEach(Array(keys.enumerated()), id: \.offset) { index, key in
                        if index > 0 { Text("+").font(Theme.font(13)) }
                        KeyCap(label: key)
                    }
                    Text("και μίλα.").font(Theme.font(13))
                    Spacer()
                    if !model.handsFreeCombo.isEmpty {
                        KeyCap(label: model.handsFreeCombo.label)
                        Text("χωρίς χέρια").font(Theme.font(13)).foregroundStyle(Theme.secondary)
                    }
                    KeyCap(label: "Esc")
                    Text("ακύρωση").font(Theme.font(13)).foregroundStyle(Theme.secondary)
                }
                .foregroundStyle(Theme.text)
            }

            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    FieldLabel("Ιστορικό")
                    Spacer()
                    HStack(spacing: 6) {
                        Image(systemName: "magnifyingglass").foregroundStyle(Theme.secondary)
                        TextField("Αναζήτηση", text: $search).textFieldStyle(.plain).font(Theme.font(13))
                    }
                    .padding(.horizontal, 10).padding(.vertical, 6)
                    .background(Theme.card)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.border))
                    .frame(width: 240)
                }
                HistoryList(entries: filtered)
            }
        }
    }

    private var filtered: [HistoryEntry] {
        let query = search.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return model.history }
        return model.history.filter { $0.text.localizedCaseInsensitiveContains(query) }
    }
}

/// Habit tracker: σερί ημερών, στόχος της ημέρας, η εβδομάδα και ημερολόγιο 20 εβδομάδων.
struct HabitTracker: View {
    let stats: DictationStats
    let goal: Int

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 16) {
                tile {
                    HStack(spacing: 14) {
                        GoalRing(progress: Double(stats.wordsToday) / Double(max(goal, 1)))
                            .frame(width: 58, height: 58)
                        VStack(alignment: .leading, spacing: 3) {
                            FieldLabel("Σήμερα")
                            Text("\(stats.wordsToday.formatted()) / \(goal.formatted())")
                                .font(Theme.number(22)).foregroundStyle(Theme.text)
                            Text(stats.wordsToday >= goal ? "Πέτυχες τον στόχο ✓" : "λέξεις · \(stats.dictationsToday) υπαγορεύσεις")
                                .font(Theme.font(12)).foregroundStyle(Theme.secondary)
                        }
                    }
                }
                tile {
                    VStack(alignment: .leading, spacing: 3) {
                        FieldLabel("Σερί")
                        HStack(alignment: .firstTextBaseline, spacing: 6) {
                            Image(systemName: "flame.fill").foregroundStyle(Theme.amber)
                            Text("\(stats.streakDays)").font(Theme.number(28)).foregroundStyle(Theme.text)
                            Text(stats.streakDays == 1 ? "μέρα" : "μέρες").font(Theme.font(13)).foregroundStyle(Theme.secondary)
                        }
                        Text("Ρεκόρ: \(stats.longestStreak) \(stats.longestStreak == 1 ? "μέρα" : "μέρες")")
                            .font(Theme.font(12)).foregroundStyle(Theme.secondary)
                    }
                }
                tile {
                    VStack(alignment: .leading, spacing: 3) {
                        FieldLabel("Χρόνος που κέρδισες")
                        Text(Self.duration(stats.minutesSaved)).font(Theme.number(24)).foregroundStyle(Theme.text)
                        Text("\(stats.totalWords.formatted()) λέξεις · \(stats.wordsPerMinute) λέξεις/λεπτό")
                            .font(Theme.font(12)).foregroundStyle(Theme.secondary)
                    }
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 14) {
                    HStack {
                        Text("Η εβδομάδα σου").font(Theme.font(15, bold: true)).foregroundStyle(Theme.text)
                        Spacer()
                        WeekStrip(wordsByDay: stats.wordsByDay, goal: goal)
                    }
                    Divider()
                    HStack(alignment: .top) {
                        Text("Τελευταίοι 6 μήνες").font(Theme.font(13, bold: true)).foregroundStyle(Theme.secondary)
                        Spacer()
                        HeatmapLegend()
                    }
                    Heatmap(wordsByDay: stats.wordsByDay)
                }
            }
        }
    }

    private func tile<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        content()
            .padding(16)
            .frame(maxWidth: .infinity, minHeight: 92, alignment: .leading)
            .background(Theme.card)
            .clipShape(RoundedRectangle(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.border))
    }

    static func duration(_ minutes: Int) -> String {
        if minutes < 60 { return "\(minutes) λεπτά" }
        let hours = minutes / 60, rest = minutes % 60
        return rest == 0 ? "\(hours) ώρ." : "\(hours) ώρ. \(rest) λ."
    }
}

struct GoalRing: View {
    let progress: Double
    var body: some View {
        ZStack {
            Circle().stroke(Theme.blue.opacity(0.12), lineWidth: 7)
            Circle().trim(from: 0, to: min(progress, 1))
                .stroke(Theme.blue, style: StrokeStyle(lineWidth: 7, lineCap: .round))
                .rotationEffect(.degrees(-90))
            if progress >= 1 {
                Image(systemName: "checkmark").font(.system(size: 16, weight: .bold)).foregroundStyle(Theme.blue)
            } else {
                Text("\(Int(progress * 100))%").font(Theme.font(12, bold: true)).foregroundStyle(Theme.text)
            }
        }
    }
}

/// Δευτέρα έως Κυριακή: γεμάτος κύκλος με ✓ = πέτυχες τον στόχο, μισογεμάτος = υπαγόρευσες,
/// άδειος = καμία υπαγόρευση. Το σχήμα και το σύμβολο λένε το ίδιο με το χρώμα.
struct WeekStrip: View {
    let wordsByDay: [Date: Int]
    let goal: Int

    var body: some View {
        let calendar = Calendar(identifier: .iso8601)
        let today = calendar.startOfDay(for: Date())
        let monday = calendar.date(from: calendar.dateComponents([.yearForWeekOfYear, .weekOfYear], from: today))!
        let names = ["Δε", "Τρ", "Τε", "Πε", "Πα", "Σα", "Κυ"]
        return HStack(spacing: 10) {
            ForEach(0..<7, id: \.self) { index in
                let day = calendar.date(byAdding: .day, value: index, to: monday)!
                let words = wordsByDay[Calendar.current.startOfDay(for: day)] ?? 0
                VStack(spacing: 4) {
                    ZStack {
                        Circle().stroke(day == today ? Theme.blue : Theme.border, lineWidth: day == today ? 2 : 1)
                        if words >= goal {
                            Circle().fill(Theme.blue).padding(2)
                            Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                        } else if words > 0 {
                            Circle().fill(Theme.sky.opacity(0.45)).padding(2)
                        }
                    }
                    .frame(width: 24, height: 24)
                    .help("\(names[index]): \(words) λέξεις")
                    Text(names[index]).font(Theme.font(11, bold: day == today)).foregroundStyle(Theme.secondary)
                }
            }
        }
    }
}

struct Heatmap: View {
    let wordsByDay: [Date: Int]
    private let weeks = 26

    static func level(_ words: Int) -> Int {
        switch words {
        case 0: return 0
        case 1..<200: return 1
        case 200..<600: return 2
        case 600..<1500: return 3
        default: return 4
        }
    }

    static func color(_ level: Int) -> Color {
        level == 0 ? Theme.blue.opacity(0.06) : Theme.blue.opacity([0, 0.25, 0.5, 0.75, 1.0][level])
    }

    var body: some View {
        let calendar = Calendar(identifier: .iso8601)
        let today = calendar.startOfDay(for: Date())
        let thisMonday = calendar.date(from: calendar.dateComponents([.yearForWeekOfYear, .weekOfYear], from: today))!
        let firstMonday = calendar.date(byAdding: .weekOfYear, value: -(weeks - 1), to: thisMonday)!
        let formatter: DateFormatter = {
            let f = DateFormatter(); f.locale = Locale(identifier: "el_GR"); f.setLocalizedDateFormatFromTemplate("EEEE d MMMM"); return f
        }()
        // Τα τετράγωνα μεγαλώνουν ώστε το ημερολόγιο να γεμίζει όλο το πλάτος της κάρτας.
        return GeometryReader { geometry in
        let cell = max(10, min(26, (geometry.size.width - 26) / CGFloat(weeks) - 4))
        HStack(alignment: .top, spacing: 4) {
            VStack(alignment: .trailing, spacing: 4) {
                ForEach(0..<7, id: \.self) { row in
                    Text(["Δε", "", "Τε", "", "Πα", "", "Κυ"][row]).font(Theme.font(9)).foregroundStyle(Theme.secondary)
                        .frame(width: 22, height: cell, alignment: .trailing)
                }
            }
            ForEach(0..<weeks, id: \.self) { week in
                VStack(spacing: 4) {
                    ForEach(0..<7, id: \.self) { row in
                        let day = calendar.date(byAdding: .day, value: week * 7 + row, to: firstMonday)!
                        let words = wordsByDay[Calendar.current.startOfDay(for: day)] ?? 0
                        RoundedRectangle(cornerRadius: 3)
                            .fill(day > today ? Color.clear : Self.color(Self.level(words)))
                            .overlay(RoundedRectangle(cornerRadius: 3)
                                .stroke(day == today ? Theme.text : .clear, lineWidth: 1.5))
                            .frame(width: cell, height: cell)
                            .help("\(formatter.string(from: day)): \(words.formatted()) λέξεις")
                    }
                }
            }
        }
        }
        .frame(height: 7 * 26 + 6 * 4)
    }
}

struct HeatmapLegend: View {
    var body: some View {
        HStack(spacing: 4) {
            Text("λιγότερο").font(Theme.font(11)).foregroundStyle(Theme.secondary)
            ForEach(0..<5, id: \.self) { level in
                RoundedRectangle(cornerRadius: 3).fill(Heatmap.color(level)).frame(width: 12, height: 12)
            }
            Text("περισσότερο").font(Theme.font(11)).foregroundStyle(Theme.secondary)
        }
    }
}

struct PermissionBanner: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Η υπαγόρευση δεν λειτουργεί ακόμα", systemImage: "exclamationmark.triangle.fill")
                .font(Theme.font(15, bold: true)).foregroundStyle(Theme.orange)
            if !model.accessibilityGranted {
                Text("Λείπει η άδεια Προσβασιμότητας. Χωρίς αυτήν η εφαρμογή δεν ακούει τη συντόμευση και δεν γράφει το κείμενο. Πάτα «Επαναφορά άδειας» και ενεργοποίησε το \(AppInfo.name) στη λίστα που θα ανοίξει.")
                    .font(Theme.font(13)).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("Επαναφορά άδειας", action: model.resetAccessibility).buttonStyle(.borderedProminent)
                    Button("Άνοιγμα ρυθμίσεων", action: model.openAccessibility)
                }
            }
            if !model.lastError.isEmpty {
                Text(model.lastError).font(Theme.font(13, bold: true)).fixedSize(horizontal: false, vertical: true)
            }
            if !model.microphoneGranted {
                Text("Λείπει η άδεια για το μικρόφωνο.").font(Theme.font(13))
                Button("Άνοιγμα ρυθμίσεων μικροφώνου", action: model.openMicrophone)
            }
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.orange.opacity(0.07))
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.orange.opacity(0.4)))
    }
}

struct HistoryList: View {
    let entries: [HistoryEntry]

    var body: some View {
        if entries.isEmpty {
            Card {
                Text("Ό,τι υπαγορεύεις θα εμφανίζεται εδώ.").font(Theme.font(13)).foregroundStyle(Theme.secondary)
            }
        } else {
            LazyVStack(alignment: .leading, spacing: 18) {
                ForEach(groups, id: \.day) { group in
                    VStack(alignment: .leading, spacing: 8) {
                        FieldLabel(label(for: group.day))
                        VStack(spacing: 0) {
                            ForEach(Array(group.entries.enumerated()), id: \.element.id) { index, entry in
                                HistoryRow(entry: entry)
                                if index < group.entries.count - 1 { Divider() }
                            }
                        }
                        .background(Theme.card)
                        .clipShape(RoundedRectangle(cornerRadius: 14))
                        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.border))
                    }
                }
            }
        }
    }

    private var groups: [(day: Date, entries: [HistoryEntry])] {
        let calendar = Calendar.current
        let grouped = Dictionary(grouping: entries.prefix(300)) { calendar.startOfDay(for: $0.date) }
        return grouped.keys.sorted(by: >).map { ($0, grouped[$0]!.sorted { $0.date > $1.date }) }
    }

    private func label(for day: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDateInToday(day) { return "Σήμερα" }
        if calendar.isDateInYesterday(day) { return "Χθες" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "el_GR")
        formatter.setLocalizedDateFormatFromTemplate("EEEE d MMMM")
        return formatter.string(from: day)
    }
}

struct HistoryRow: View {
    let entry: HistoryEntry
    @State private var hovering = false
    @State private var copied = false

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            Text(entry.date.formatted(date: .omitted, time: .shortened))
                .font(Theme.font(12).monospacedDigit()).foregroundStyle(Theme.secondary)
                .frame(width: 56, alignment: .leading)
            Text(entry.text)
                .font(Theme.font(13)).foregroundStyle(Theme.text)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
            Button {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(entry.text, forType: .string)
                copied = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { copied = false }
            } label: {
                Label(copied ? "Αντιγράφηκε" : "Αντιγραφή", systemImage: copied ? "checkmark" : "doc.on.doc")
                    .font(Theme.font(12))
            }
            .buttonStyle(.borderless)
            .opacity(hovering || copied ? 1 : 0)
        }
        .padding(.horizontal, 16).padding(.vertical, 12)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}

// MARK: - Λεξιλόγιο

struct DictionaryView: View {
    @ObservedObject var model: AppModel
    @State private var newWord = ""
    @State private var newFrom = ""
    @State private var newTo = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            HStack(alignment: .top) {
                SectionTitle("Λεξιλόγιο", "Ονόματα και όροι που λες συχνά. Τα στέλνουμε στο μοντέλο, ώστε να τα γράφει σωστά.")
                Spacer()
                Button("Εισαγωγή από Wispr Flow", action: model.importFromWispr)
            }

            Card {
                VStack(alignment: .leading, spacing: 14) {
                    Text("Λέξεις (\(model.vocabulary.words.count))").font(Theme.font(15, bold: true))
                    HStack {
                        TextField("Νέα λέξη, π.χ. Higgsfield (Enter για προσθήκη)", text: $newWord)
                            .textFieldStyle(.roundedBorder).font(Theme.font(13))
                            .onSubmit(addWord)
                        Button("Προσθήκη", action: addWord)
                            .buttonStyle(.borderedProminent).tint(Theme.blue)
                            .disabled(newWord.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                    WrapLayout(spacing: 8) {
                        ForEach(model.vocabulary.words, id: \.self) { word in
                            HStack(spacing: 6) {
                                Text(word).font(Theme.font(13)).foregroundStyle(Theme.text)
                                Button {
                                    model.vocabulary.words.removeAll { $0 == word }
                                } label: { Image(systemName: "xmark").font(.system(size: 9, weight: .bold)) }
                                .buttonStyle(.plain).foregroundStyle(Theme.secondary)
                                .help("Αφαίρεση")
                            }
                            .padding(.horizontal, 10).padding(.vertical, 6)
                            .background(Theme.chip)
                            .clipShape(Capsule())
                            .overlay(Capsule().stroke(Theme.blue.opacity(0.22)))
                        }
                    }
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 14) {
                    Text("Διορθώσεις (\(model.vocabulary.replacements.count))").font(Theme.font(15, bold: true))
                    Text("Όταν το μοντέλο γράψει την αριστερή λέξη, τη διορθώνουμε στη δεξιά.")
                        .font(Theme.font(13)).foregroundStyle(Theme.secondary)
                    HStack {
                        TextField("Γράφει λάθος, π.χ. key.ai", text: $newFrom).textFieldStyle(.roundedBorder)
                        Image(systemName: "arrow.right").foregroundStyle(Theme.secondary)
                        TextField("Σωστό, π.χ. kie.ai", text: $newTo).textFieldStyle(.roundedBorder).onSubmit(addReplacement)
                        Button("Προσθήκη", action: addReplacement)
                            .buttonStyle(.borderedProminent).tint(Theme.blue)
                            .disabled(newFrom.isEmpty || newTo.isEmpty)
                    }
                    .font(Theme.font(13))
                    ForEach(Array(model.vocabulary.replacements.enumerated()), id: \.offset) { index, rule in
                        HStack {
                            Text(rule.from).font(Theme.font(13)).frame(width: 220, alignment: .leading)
                            Image(systemName: "arrow.right").foregroundStyle(Theme.secondary)
                            Text(rule.to).font(Theme.font(13, bold: true))
                            Spacer()
                            Button { model.vocabulary.replacements.remove(at: index) } label: { Image(systemName: "trash") }
                                .buttonStyle(.borderless).help("Αφαίρεση")
                        }
                        if index < model.vocabulary.replacements.count - 1 { Divider() }
                    }
                }
            }
        }
    }

    private func addWord() {
        let word = newWord.trimmingCharacters(in: .whitespaces)
        guard !word.isEmpty, !model.vocabulary.words.contains(where: { $0.lowercased() == word.lowercased() }) else {
            newWord = ""; return
        }
        model.vocabulary.words.insert(word, at: 0)
        newWord = ""
    }

    private func addReplacement() {
        let from = newFrom.trimmingCharacters(in: .whitespaces), to = newTo.trimmingCharacters(in: .whitespaces)
        guard !from.isEmpty, !to.isEmpty else { return }
        model.vocabulary.replacements.removeAll { $0.from.lowercased() == from.lowercased() }
        model.vocabulary.replacements.insert(.init(from: from, to: to), at: 0)
        newFrom = ""; newTo = ""
    }
}

/// Στοίχιση σε γραμμές που αναδιπλώνονται, για τις λέξεις του λεξιλογίου.
struct WrapLayout: Layout {
    var spacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 600
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x + size.width > width, x > 0 { x = 0; y += rowHeight + spacing; rowHeight = 0 }
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
        return CGSize(width: width, height: y + rowHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, rowHeight: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x + size.width > bounds.maxX, x > bounds.minX { x = bounds.minX; y += rowHeight + spacing; rowHeight = 0 }
            view.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}

// MARK: - Snippets

struct SnippetsView: View {
    @ObservedObject var model: AppModel
    @State private var trigger = ""
    @State private var text = ""
    @State private var editing: String?   // η φράση του έτοιμου κειμένου που διορθώνεις
    @State private var saved: String?
    @FocusState private var focus: Field?
    private enum Field { case trigger, text }

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            SectionTitle("Έτοιμα κείμενα", "Λες μια σύντομη φράση και γράφεται ολόκληρο το κείμενο. Για email, συνδέσμους και απαντήσεις που γράφεις συχνά.")

            Card {
                VStack(alignment: .leading, spacing: 14) {
                    Text(editing == nil ? "Νέο έτοιμο κείμενο" : "Διόρθωση έτοιμου κειμένου").font(Theme.font(15, bold: true))
                    VStack(alignment: .leading, spacing: 6) {
                        FieldLabel("Όταν λέω")
                        TextField("π.χ. ξέρω γεια", text: $trigger)
                            .textFieldStyle(.roundedBorder).font(Theme.font(13))
                            .focused($focus, equals: .trigger)
                            .onSubmit { focus = .text }
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        FieldLabel("Να γράφεται")
                        ZStack(alignment: .topLeading) {
                            TextEditor(text: $text)
                                .font(Theme.font(13))
                                .scrollContentBackground(.hidden)
                                .padding(6)
                                .focused($focus, equals: .text)
                            if text.isEmpty {
                                Text("Το κείμενο που θα μπαίνει, π.χ. ένα email ή έναν σύνδεσμο")
                                    .font(Theme.font(13)).foregroundStyle(Theme.secondary.opacity(0.7))
                                    .padding(.horizontal, 11).padding(.vertical, 6)
                                    .allowsHitTesting(false)
                            }
                        }
                        .frame(height: 96)
                        .background(Theme.background)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                        .overlay(RoundedRectangle(cornerRadius: 8).stroke(focus == .text ? Theme.blue : Theme.border))
                    }
                    HStack(spacing: 10) {
                        Button(editing == nil ? "Αποθήκευση" : "Αποθήκευση αλλαγών", action: save)
                            .buttonStyle(.borderedProminent).tint(Theme.blue)
                            .keyboardShortcut(.return, modifiers: .command)
                            .disabled(trigger.trimmingCharacters(in: .whitespaces).isEmpty
                                      || text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        if editing != nil {
                            Button("Άκυρο") { editing = nil; trigger = ""; text = "" }
                        }
                        Text("⌘ + Enter για αποθήκευση").font(Theme.font(12)).foregroundStyle(Theme.secondary)
                        if let saved {
                            Label("Αποθηκεύτηκε: «\(saved)»", systemImage: "checkmark.circle.fill")
                                .font(Theme.font(12, bold: true)).foregroundStyle(Theme.blue)
                        }
                    }
                }
            }

            FieldLabel("Τα έτοιμα κείμενά σου (\(model.vocabulary.snippets.count))")
            ForEach(Array(model.vocabulary.snippets.enumerated()), id: \.offset) { index, snippet in
                Card {
                    HStack(alignment: .top, spacing: 14) {
                        VStack(alignment: .leading, spacing: 6) {
                            HStack(spacing: 6) {
                                Image(systemName: "mic.fill").font(.system(size: 11)).foregroundStyle(Theme.blue)
                                Text("«\(snippet.from)»").font(Theme.font(13, bold: true)).foregroundStyle(Theme.text)
                            }
                            Text(snippet.to).font(Theme.font(13)).foregroundStyle(Theme.secondary)
                                .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer()
                        Button {
                            editing = snippet.from; trigger = snippet.from; text = snippet.to; saved = nil; focus = .text
                        } label: { Image(systemName: "pencil") }
                            .buttonStyle(.borderless).help("Διόρθωση")
                        Button { model.vocabulary.snippets.remove(at: index) } label: { Image(systemName: "trash") }
                            .buttonStyle(.borderless).help("Αφαίρεση")
                    }
                }
            }
        }
    }

    private func save() {
        let phrase = trigger.trimmingCharacters(in: .whitespaces)
        let body = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !phrase.isEmpty, !body.isEmpty else { return }
        let previous = editing
        model.vocabulary.snippets.removeAll { snippet in
            snippet.from.lowercased() == phrase.lowercased() || snippet.from == previous
        }
        model.vocabulary.snippets.insert(.init(from: phrase, to: body), at: 0)
        saved = phrase
        editing = nil
        trigger = ""; text = ""
        focus = .trigger
    }
}

// MARK: - Ρυθμίσεις

struct SettingsView: View {
    @ObservedObject var model: AppModel
    @State private var confirmClear = false

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            SectionTitle("Ρυθμίσεις")

            Card {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Μοντέλο ομιλίας").font(Theme.font(15, bold: true))
                    ModelOption(title: "Scribe v2 της ElevenLabs",
                                detail: "Το καλύτερο για ελληνικά με αγγλικούς όρους. Χρεώνεται ανά λεπτό ήχου, με κλειδί από την ElevenLabs. Ο ήχος στέλνεται στην ElevenLabs.",
                                selected: model.speechModel == .scribe,
                                enabled: model.hasElevenLabsKey) { model.speechModel = .scribe }
                    ModelOption(title: "Whisper της OpenAI",
                                detail: "Δωρεάν και πιο γρήγορο, τρέχει στον υπολογιστή σου χωρίς ίντερνετ. Πιάνει ~3 GB μνήμης όσο είναι επιλεγμένο.",
                                selected: model.speechModel == .whisper,
                                enabled: true) { model.speechModel = .whisper }
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 14) {
                    Text("Στόχος").font(Theme.font(15, bold: true))
                    Stepper(value: $model.dailyGoal, in: 100...20_000, step: 100) {
                        Text("Λέξεις την ημέρα: \(model.dailyGoal.formatted())").font(Theme.font(13))
                    }
                    Text("Μετράει στον δακτύλιο «Σήμερα» και στα ✓ της εβδομάδας στην Αρχική.")
                        .font(Theme.font(12)).foregroundStyle(Theme.secondary)
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 14) {
                    Text("Μικρόφωνο και ήχοι").font(Theme.font(15, bold: true))
                    Picker("Μικρόφωνο", selection: $model.microphoneUID) {
                        Text("Προεπιλογή του συστήματος").tag("")
                        ForEach(model.inputDevices) { device in Text(device.name).tag(device.uid) }
                    }
                    .font(Theme.font(13))
                    Toggle("Ήχος στην αρχή και στο τέλος της ηχογράφησης", isOn: $model.soundsEnabled)
                        .font(Theme.font(13))
                    Toggle("Άνοιγμα με την εκκίνηση του Mac", isOn: Binding(
                        get: { model.launchAtLogin }, set: { model.setLaunchAtLogin($0) }))
                        .font(Theme.font(13))
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 10) {
                    Text("Συντομεύσεις").font(Theme.font(15, bold: true))
                    shortcutRow("Κράτα πατημένα και μίλα", "ptt", model.pushToTalkCombo, Combo.parse(Combo.defaultPushToTalk))
                    shortcutRow("Χωρίς χέρια (ξανά για τέλος)", "hands", model.handsFreeCombo, Combo.parse(Combo.defaultHandsFree))
                    shortcut(["Esc"], "Ακύρωση")
                    if !model.shortcutError.isEmpty {
                        Label(model.shortcutError, systemImage: "exclamationmark.triangle")
                            .font(Theme.font(12)).foregroundStyle(Theme.orange).fixedSize(horizontal: false, vertical: true)
                    }
                    Text("Πάτα «Αλλαγή» και μετά τα πλήκτρα που θέλεις (π.χ. Control + Space, δεξί Option, F5) ή ένα κουμπί του ποντικιού. Τα πλήκτρα της συντόμευσης δεν φτάνουν πια στις άλλες εφαρμογές, ούτε το κουμπί του ποντικιού που διαλέγεις.")
                        .font(Theme.font(12)).foregroundStyle(Theme.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Άδειες").font(Theme.font(15, bold: true))
                    permission("Προσβασιμότητα", granted: model.accessibilityGranted) {
                        HStack {
                            Button("Επαναφορά άδειας", action: model.resetAccessibility)
                            Button("Άνοιγμα ρυθμίσεων", action: model.openAccessibility)
                        }
                    }
                    permission("Μικρόφωνο", granted: model.microphoneGranted) {
                        Button("Άνοιγμα ρυθμίσεων", action: model.openMicrophone)
                    }
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Δεδομένα").font(Theme.font(15, bold: true))
                    Text("Το ιστορικό και το λεξιλόγιο μένουν μόνο στον υπολογιστή σου. Ο ήχος δεν αποθηκεύεται.")
                        .font(Theme.font(13)).foregroundStyle(Theme.secondary)
                    HStack {
                        Button("Αρχείο καταγραφής", action: model.openLog)
                        Button("Διαγραφή ιστορικού") { confirmClear = true }
                    }
                    if !model.lastError.isEmpty {
                        Label(model.lastError, systemImage: "exclamationmark.triangle")
                            .font(Theme.font(12)).foregroundStyle(Theme.orange)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .confirmationDialog("Να διαγραφεί όλο το ιστορικό;", isPresented: $confirmClear) {
                Button("Διαγραφή", role: .destructive) { model.history = [] }
            }
        }
        .onAppear { model.refreshDevices() }
        .onDisappear { if model.capturing != nil { model.stopCapture() } }
    }

    private func shortcutRow(_ title: String, _ which: String, _ combo: Combo, _ fallback: Combo) -> some View {
        HStack(spacing: 6) {
            Text(title).font(Theme.font(13)).frame(width: 230, alignment: .leading)
            if model.capturing == which {
                Text("Πάτα τα πλήκτρα ή ένα κουμπί του ποντικιού… (Esc για ακύρωση)")
                    .font(Theme.font(13, bold: true)).foregroundStyle(Theme.blue)
            } else {
                ForEach(Array(combo.keys.enumerated()), id: \.offset) { index, key in
                    if index > 0 { Text("+").font(Theme.font(13)) }
                    KeyCap(label: key)
                }
            }
            Spacer()
            if model.capturing == which {
                Button("Άκυρο") { model.stopCapture() }
            } else {
                Button("Αλλαγή") { model.startCapture(which) }
                if combo != fallback { Button("Προεπιλογή") { model.setShortcut(which, fallback) } }
                if which == "hands" && !combo.isEmpty { Button("Καμία") { model.setShortcut(which, .none) } }
            }
        }
    }

    private func shortcut(_ keys: [String], _ meaning: String) -> some View {
        HStack(spacing: 6) {
            ForEach(Array(keys.enumerated()), id: \.offset) { index, key in
                if index > 0 { Text("+").font(Theme.font(13)) }
                KeyCap(label: key)
            }
            Text(meaning).font(Theme.font(13)).foregroundStyle(Theme.secondary).padding(.leading, 8)
        }
    }

    private func permission<Actions: View>(_ title: String, granted: Bool, @ViewBuilder actions: () -> Actions) -> some View {
        HStack {
            Image(systemName: granted ? "checkmark.circle.fill" : "xmark.circle.fill")
                .foregroundStyle(granted ? Theme.blue : Theme.orange)
            Text(title).font(Theme.font(13, bold: true))
            Text(granted ? "δόθηκε" : "λείπει").font(Theme.font(13)).foregroundStyle(Theme.secondary)
            Spacer()
            if !granted { actions() }
        }
    }
}

struct ModelOption: View {
    let title: String
    let detail: String
    let selected: Bool
    let enabled: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .font(.system(size: 16)).foregroundStyle(selected ? Theme.blue : Theme.secondary)
                VStack(alignment: .leading, spacing: 4) {
                    Text(title).font(Theme.font(13, bold: true)).foregroundStyle(Theme.text)
                    Text(enabled ? detail : "Λείπει το κλειδί της ElevenLabs.")
                        .font(Theme.font(12)).foregroundStyle(Theme.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer()
            }
            .padding(12)
            .background(selected ? Theme.blue.opacity(0.06) : .clear)
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(selected ? Theme.blue.opacity(0.5) : Theme.border))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.5)
    }
}

// MARK: - Παράθυρο εφαρμογής

final class MainWindowController: NSObject, NSWindowDelegate {
    private var window: NSWindow?
    private let model: AppModel

    init(model: AppModel) { self.model = model }

    func show() {
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1080, height: 760),
                                  styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                                  backing: .buffered, defer: false)
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.title = AppInfo.name
            window.appearance = NSAppearance(named: .aqua)
            window.contentViewController = NSHostingController(rootView: RootView(model: model))
            window.setContentSize(NSSize(width: 1080, height: 760))
            window.isReleasedWhenClosed = false
            window.delegate = self
            window.center()
            self.window = window
        }
        // Όσο είναι ανοιχτό το παράθυρο, η εφαρμογή φαίνεται στο Dock και στο Cmd+Tab.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
        window?.orderFrontRegardless()
    }

    func windowWillClose(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
    }
}
