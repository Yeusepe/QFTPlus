package com.qftplus.headset

import android.content.Intent
import android.os.Bundle
import android.view.ViewGroup
import android.widget.TextView
import androidx.compose.ui.platform.ComposeView
import androidx.activity.ComponentActivity
import android.text.method.DigitsKeyListener
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import org.json.JSONObject
import java.util.Locale

class MainActivity : ComponentActivity() {
    private val settings by lazy { getSharedPreferences("settings", 0) }
    private var resumed by mutableStateOf(false)
    private var notice by mutableStateOf("")
    private var resetting by mutableStateOf(false)
    private val pages = listOf("Tracking", "Adjustments", "Calibration", "Connections")
    private val icons = listOf("oc_icon_face_tracking_on_filled_24", "tune", "frame_person", "wifi_tethering")
    private val areaIcons = mapOf("Brows" to "eyebrow", "Eyelids" to "visibility", "Gaze" to "eye_tracking", "Pupils" to "adjust", "Nose" to "face",
        "Cheeks" to "sentiment_satisfied", "Lips" to "lips", "Mouth" to "mood", "Jaw" to "dentistry", "Tongue" to "sentiment_excited")
    private var tab by mutableIntStateOf(0)
    private var root by mutableIntStateOf(0)
    private var query by mutableStateOf("")
    private var focus by mutableStateOf<String?>(null)
    private var openPath = ""
    private lateinit var ocui: Ocui
    private val parameters by lazy { FaceExpressions(false).read(ByteArray(608), 0).keys.toList() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        tab = (savedInstanceState?.getInt("page") ?: intent.getIntExtra("page", 0)).coerceIn(0, pages.lastIndex)
        try {
            ocui = Ocui(this)
            setContentView(ocui.root)
            ocui.root.addView(ComposeView(this).apply { setContent { Panel() } }, ViewGroup.LayoutParams(-1, -1))
        } catch (e: Exception) {
            android.util.Log.e("QFT-OCUI", "Settings UI unavailable", e)
            setContentView(android.widget.LinearLayout(this).apply {
                orientation = android.widget.LinearLayout.VERTICAL
                setPadding(32, 32, 32, 32)
                addView(TextView(this@MainActivity).apply {
                    text = "QFT+ needs a compatible headset Settings UI.\n\n${e.message}\n\nUpdate QFT+ and reopen it. Your saved calibration is unchanged."
                    textSize = 22f
                })
                addView(android.widget.Button(this@MainActivity).apply {
                    text = "Stop tracking"; setOnClickListener { stop(); text = "Tracking stopped" }
                })
            })
            return
        }
        if (checkSelfPermission("android.permission.POST_NOTIFICATIONS") != 0)
            requestPermissions(arrayOf("android.permission.POST_NOTIFICATIONS"), 1)
        if (settings.getBoolean("enabled", false)) start()
    }
    private fun start() {
        if (resetting) return
        settings.edit().putBoolean("enabled", true).apply()
        notice = ""
        startForegroundService(Intent(this, TrackingService::class.java))
    }
    private fun stop() { settings.edit().putBoolean("enabled", false).apply(); stopService(Intent(this, TrackingService::class.java)) }
    private fun restartTracking() { if (settings.getBoolean("enabled", false)) { stop(); start() } }
    private val rates = listOf(0, 30, 20)
    private val rateNames = listOf("Fastest", "30 per second", "20 per second")
    private fun begin(pupils: Boolean) {
        if (resetting) return
        startCalibration(this, pupils)?.let { notice = it; return }
        finishAndRemoveTask()
    }

    private fun interface RowContent { @Composable fun Draw() }
    @Composable private fun Group(header: String? = null, footer: String? = null, vararg rows: RowContent) =
        ocui.Section(header, footer) { rows.forEach { it.Draw() } }
    @OptIn(ExperimentalFoundationApi::class)
    private fun row(title: String, subtitle: String? = null, icon: String? = null, trailing: Ocui.Slot? = null, below: Ocui.Slot? = null, onClick: (() -> Unit)? = null) =
        RowContent {
            val found = title == focus
            val requester = remember { BringIntoViewRequester() }
            if (found) LaunchedEffect(Unit) { delay(150); requester.bringIntoView() }
            ocui.Row(title, subtitle, icon, trailing, below, Modifier.bringIntoViewRequester(requester), found, onClick)
        }
    @Composable private fun preference(key: String): MutableState<Boolean> = remember(key) { mutableStateOf(settings.getBoolean(key, false)) }
    private fun switchRow(title: String, subtitle: String, state: MutableState<Boolean>, key: String) =
        row(title, subtitle, trailing = ocui.toggle(state.value, title) { state.value = it; settings.edit().putBoolean(key, it).apply() })
    private fun numberRow(title: String, value: Float, range: ClosedFloatingPointRange<Float>, shown: (Float) -> String, change: (Float) -> Unit) =
        row(title, shown(value), below = ocui.slider((value - range.start) / (range.endInclusive - range.start), title) {
            change(range.start + it * (range.endInclusive - range.start))
        })

    @Composable private fun Panel() {
        LaunchedEffect(focus) { if (focus != null) { delay(2500); focus = null } }
        if (query.isNotEmpty()) BackHandler { query = "" }
        BoxWithConstraints(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)) {
            val compact = maxWidth < 720.dp
            Row(Modifier.fillMaxSize()) {
                if (!compact) Box(Modifier.width(272.dp).fillMaxHeight().background(androidx.compose.ui.graphics.Color(ocui.sidebar))) {
                    Column(Modifier.fillMaxSize()) {
                        Search(Modifier.padding(start = 20.dp, top = 32.dp, end = 20.dp).fillMaxWidth())
                        ocui.Navigation(pages, icons, tab, ::open, Modifier.fillMaxWidth().weight(1f))
                    }
                }
                Box(Modifier.weight(1f).fillMaxHeight().padding(start = if (compact) 16.dp else 24.dp, end = if (compact) 16.dp else 32.dp, top = 24.dp), Alignment.TopCenter) {
                    Column(Modifier.widthIn(max = 720.dp).fillMaxSize()) {
                        if (compact) Row(Modifier.fillMaxWidth().padding(bottom = 8.dp), Arrangement.spacedBy(16.dp), Alignment.CenterVertically) {
                            Search(Modifier.weight(1f))
                            ocui.Dropdown(pages, tab, ::open, "Page", Modifier.widthIn(max = 230.dp))
                        }
                        if (query.isNotBlank()) SearchPage()
                        else key(tab, root) {
                            when (tab) {
                                0 -> Page(pages[0]) { TrackingPage() }
                                1 -> AdjustmentsPage()
                                2 -> Page(pages[2]) { CalibrationPage() }
                                else -> ConnectionsPage()
                            }
                        }
                    }
                }
            }
        }
    }
    private fun open(page: Int) { if (page == tab) root++; tab = page; notice = ""; query = "" }
    @Composable private fun Page(title: String, back: (() -> Unit)? = null, content: @Composable ColumnScope.() -> Unit) = Column(Modifier.fillMaxSize()) {
        ocui.Header(title, back)
        Column(Modifier.fillMaxWidth().weight(1f).verticalScroll(rememberScrollState()).imePadding().padding(bottom = 20.dp),
            verticalArrangement = Arrangement.spacedBy(32.dp)) {
            if (notice.isNotEmpty()) Group(null, null, row(notice, icon = "oc_icon_info_filled_24"))
            content()
        }
    }

    private class Hit(val title: String, val where: String?, val tab: Int, val path: String = "", val row: String? = title, val words: String = "")
    private fun index(): List<Hit> {
        val hits = mutableListOf(
            Hit("Face and eye tracking", null, 0, words = "on off start stop status"),
            Hit("Pupil dilation", "Features", 0, words = "eyes pupils"),
            Hit("Convergence", "Gaze", 1, "Gaze", words = "independent eye gaze depth startup"),
            Hit("Convergence strength", "Gaze", 1, "Gaze", words = "eyes depth gain vergence"),
            Hit("Reset to 100%", "Gaze", 1, "Gaze", words = "convergence strength default"),
            Hit("Tongue direction", "Features", 0),
            Hit("Resume after restart", "Features", 0, words = "boot startup reboot automatically"),
            Hit("Update rate", "Performance", 0, words = "fps frame rate speed latency smooth battery heat"),
            Hit("Face and tongue", null, 2, words = "calibrate mouth avatar expressions"),
            Hit("Pupil dilation", null, 2, words = "calibrate eyes pupils"),
            Hit("Avatar distance", "Calibration space", 2),
            Hit("Reduce motion", "Calibration space", 2, words = "animation"),
            Hit("Remove saved calibration", null, 2, words = "delete reset clear"),
            Hit("VRChat", "Send tracking to", 3, words = "output destination osc vrcft"),
            Hit("OSC apps", "Send tracking to", 3, words = "output destination raw"),
            Hit("QFT+ on your PC", "Send tracking to", 3, words = "output destination pair computer vrcfacetracking module"))
        if (TrackingService.outputMode(settings) != "pc") hits += listOf(
            Hit("New receiver", "My receiver", 3, words = "add manual ip address port host"),
            Hit("Other receivers", null, 3, row = "Looking for receivers on this network…", words = "find search discover network osc connect"))
        adjustable()?.let { data ->
            hits += Hit("All parameters", null, 1, "*", null, "defaults every")
            data.areas.filter { a -> data.parameters.any { it.area == a } }.forEach { hits += Hit(it, "Areas", 1, it, null) }
            data.parameters.forEach { hits += Hit(label(it.name), it.area, 1, it.name, null, it.name) }
            data.parameters.mapNotNull { p -> pair(p)?.let { it to p.area } }.distinct().forEach { (pair, area) -> hits += Hit(pair, area, 1, pair, null, "together left right eyes") }
            listOf("Headset passthrough" to "source meta native", "Match left and right" to "source average stronger side", "Strength" to "output gain multiplier",
                "Offset" to "output shift", "Dead zone" to "output deadzone", "Smoothing" to "output filter jitter", "Response curve" to "response gamma",
                "Smoothing when relaxing" to "response release", "Invert output" to "response reverse", "Restore defaults" to "reset")
                .forEach { (title, words) -> hits += Hit(title, "All parameters", 1, "*", title, words) }
            listOf("Input minimum", "Input maximum", "Resting input", "Output minimum", "Output maximum")
                .forEach { hits += Hit(it, "Limits, in each area and parameter", 1, "", null, "range clamp neutral") }
        }
        return hits
    }
    private fun search(text: String): List<Hit> {
        val phrase = text.trim().lowercase(); val terms = phrase.split(' ').filter { it.isNotEmpty() }
        return index().mapNotNull { hit ->
            val title = hit.title.lowercase()
            val all = "$title ${hit.where.orEmpty()} ${pages[hit.tab]} ${hit.words}".lowercase()
            if (terms.any { it !in all }) null
            else hit to when { title.startsWith(phrase) -> 0; title.split(' ').any { it.startsWith(terms[0]) } -> 1; terms.all { it in title } -> 2; else -> 3 }
        }.sortedBy { it.second }.map { it.first }
    }
    private fun go(hit: Hit) { tab = hit.tab; openPath = hit.path; focus = hit.row; root++; query = ""; notice = "" }
    @Composable private fun Search(modifier: Modifier) = ocui.SearchField(query, { query = it }, { search(query).firstOrNull()?.let(::go) }, "Search QFT+", modifier)

    @Composable private fun SearchPage() {
        val hits = remember(query) { search(query) }
        Page("Search results") {
            if (hits.isEmpty()) Group(null, "Check the spelling, or search for a setting, area or parameter name.", row("No results for “${query.trim()}”"))
            else hits.map { it.tab }.distinct().forEach { page ->
                Group(pages[page], null, *hits.filter { it.tab == page }.map { hit -> row(hit.title, hit.where, trailing = ocui.chevron()) { go(hit) } }.toTypedArray())
            }
        }
    }

    @Composable private fun TrackingPage() {
        var status by remember { mutableStateOf(TrackingService.status) }
        var enabled by remember { mutableStateOf(settings.getBoolean("enabled", false)) }
        val pupils = preference("pupils"); val tongue = preference("tongue"); val boot = preference("boot")
        var rate by remember { mutableIntStateOf(settings.getInt("rate", 0)) }
        LaunchedEffect(resumed) { while (resumed) {
            status = TrackingService.status; enabled = settings.getBoolean("enabled", false); rate = settings.getInt("rate", 0)
            for ((state, key) in listOf(pupils to "pupils", tongue to "tongue", boot to "boot")) state.value = settings.getBoolean(key, false)
            delay(500)
        } }
        Group(null, "Turns on the headset's face and eye tracking while it runs, and off again when you stop it.",
            row("Face and eye tracking", if (enabled) status else TrackingService.error?.let { "Stopped: $it" } ?: "Off", trailing = ocui.toggle(enabled, "Face and eye tracking") {
                if (it) start() else stop(); enabled = settings.getBoolean("enabled", false)
            }))
        Group("Features", null,
            switchRow("Pupil dilation", "Uses your pupil calibration.", pupils, "pupils"),
            switchRow("Tongue direction", "Uses your face calibration.", tongue, "tongue"),
            switchRow("Resume after restart", "Turns tracking back on if it was on when the headset restarted.", boot, "boot"))
        Group("Performance", "How often your face is measured and sent. Faster rates follow your face more closely; slower rates use less battery and keep the headset cooler.",
            row("Update rate", trailing = ocui.select(rateNames, rates.indexOf(rate).coerceAtLeast(0), "Update rate") { i ->
                rate = rates[i]; settings.edit().putInt("rate", rate).apply(); restartTracking()
            }))
    }

    @Composable private fun ConvergenceGroups() {
        var convergence by remember { mutableStateOf(settings.getBoolean("convergence", true)) }
        var vergenceGain by remember { mutableFloatStateOf(settings.getFloat("vergenceGain", 1f)) }
        LaunchedEffect(resumed) { while (resumed) {
            convergence = settings.getBoolean("convergence", true); vergenceGain = settings.getFloat("vergenceGain", 1f); delay(500)
        } }
        Group("Convergence", null,
            row("Convergence", "Tracks each eye separately for depth. Starts with tracking when enabled.", trailing = ocui.toggle(convergence, "Convergence") {
                convergence = it; settings.edit().putBoolean("convergence", it).apply(); restartTracking()
            }))
        if (convergence) Group(null, "100% uses the calibrated movement. At 0%, both eyes look in the same direction. Depth accuracy is experimental. Changes are saved and apply immediately.",
            numberRow("Convergence strength", vergenceGain, 0f..3f, { "${(it * 100).toInt()}%" }) {
                vergenceGain = it; settings.edit().putFloat("vergenceGain", it).apply()
            },
            row("Reset to 100%", trailing = ocui.button("Reset") { vergenceGain = 1f; settings.edit().putFloat("vergenceGain", 1f).apply() }))
    }

    @Composable private fun CalibrationPage() {
        var confirm by remember { mutableStateOf(false) }
        var distance by remember { mutableFloatStateOf(settings.getFloat("guideDistance", .75f).coerceIn(.5f, 1.2f)) }
        val reduceMotion = preference("reduceMotion")
        Group(null, "Calibrate after changing how the headset fits. Your saved calibration changes only when a new one passes.",
            row("Face and tongue", "Copy 12 expressions from a Meta avatar · about 75 seconds", "oc_icon_face_tracking_on_filled_24",
                ocui.button("Calibrate", "PRIMARY", !resetting) { begin(false) }),
            row("Pupil dilation", "Follow a dot as the space darkens and brightens · about 80 seconds", "adjust",
                ocui.button("Calibrate", enabled = !resetting) { begin(true) }))
        Group("Calibration space", "Each calibration shows a different Meta preset avatar in your headset's setup space.",
            numberRow("Avatar distance", distance, .5f..1.2f, { String.format(Locale.getDefault(), "%.2f m", it) }) {
                distance = it; settings.edit().putFloat("guideDistance", it).apply()
            },
            switchRow("Reduce motion", "Shows each expression without animating into it.", reduceMotion, "reduceMotion"))
        Group(null, null, row("Remove saved calibration", "Stops tracking and removes your face and pupil calibration.",
            trailing = ocui.button("Remove", "DANGER", !resetting) { confirm = true }))
        if (confirm) ocui.Dialog("Remove calibration?",
            "This removes your saved face and pupil calibration and stops tracking. You can calibrate again at any time.", "Remove", { confirm = false }) {
            confirm = false
            if (!resetting) {
                resetting = true; stop()
                Thread {
                    try { TrackingService.removeCalibration(this); runOnUiThread { notice = "Saved calibration removed." } }
                    catch (e: Exception) { runOnUiThread { notice = "Couldn't remove calibration: ${e.message}" } }
                    finally { runOnUiThread { resetting = false } }
                }.start()
            }
        }
    }

    @Composable private fun ConnectionsPage() {
        var mode by remember { mutableStateOf(TrackingService.outputMode(settings)) }
        var destinations by remember { mutableStateOf<List<OscDiscovery.Destination>>(emptyList()) }
        var editing by rememberSaveable { mutableStateOf<String?>(null) }
        var revision by remember { mutableIntStateOf(0) }
        var sending by remember { mutableStateOf(false) }
        LaunchedEffect(resumed) { while (resumed) { sending = settings.getBoolean("enabled", false) && TrackingService.status.startsWith("Tracking on headset"); delay(1000) } }
        DisposableEffect(resumed, mode, editing) {
            val discovery = if (resumed && mode != "pc" && editing == null) OscDiscovery(this@MainActivity) { destinations = it } else null
            onDispose { discovery?.close() }
        }
        val host = remember(revision) { settings.getString("oscHost", "") ?: "" }
        val port = remember(revision) { settings.getInt("oscPort", 9000) }
        val name = remember(revision) { settings.getString("oscName", null) ?: settings.getString("oscService", null) ?: "$host:$port" }
        fun save(address: String, number: Int, service: OscDiscovery.Destination? = null) {
            val tracking = settings.getBoolean("enabled", false)
            stop()
            settings.edit().putString("oscHost", address).putInt("oscPort", number).apply {
                if (service != null) putString("oscService", service.service).putString("oscServiceType", service.type).putString("oscName", service.name)
                else remove("oscService").remove("oscServiceType").remove("oscName")
            }.apply()
            if (tracking) start()
            revision++; editing = null
        }
        val savedService = remember(revision) { settings.getString("oscService", null) }
        val savedType = remember(revision) { settings.getString("oscServiceType", null) }
        LaunchedEffect(destinations) { destinations.firstOrNull { it.service == savedService && it.type == savedType && (it.host != host || it.port != port) }?.let { save(it.host, it.port, it) } }
        editing?.let { which -> ReceiverPage(if (which == "saved") name else null, if (which == "saved") host else "", if (which == "saved") port.toString() else "9000",
            back = { editing = null }, save = { h, p -> save(h, p) }, forget = { stop(); settings.edit().remove("oscHost").remove("oscService").remove("oscServiceType").remove("oscName").apply(); revision++; editing = null }); return }
        fun choose(id: String) { if (mode != id) { stop(); mode = id; settings.edit().putString("output", id).apply(); notice = "Destination changed. Turn tracking on when you're ready." } }
        val paired = !settings.getString("key", "").isNullOrEmpty()
        Page(pages[3]) {
            Group("Send tracking to", null,
                row("VRChat", "Standard VRCFT v2 parameters over OSC", "sports_esports", trailing = ocui.radio(mode == "vrchat", "VRChat") { choose("vrchat") }) { choose("vrchat") },
                row("OSC apps", "Named /qft values", "apps", trailing = ocui.radio(mode == "raw", "OSC apps") { choose("raw") }) { choose("raw") },
                row("QFT+ on your PC", if (paired) "Paired · connects automatically" else "Pair from Settings in QFT+ on your PC", "oc_icon_computer_filled_24",
                    trailing = ocui.radio(mode == "pc", "QFT+ on your PC") { choose("pc") }) { choose("pc") })
            if (mode == "pc") return@Page
            Group("My receiver", null, *listOfNotNull(
                if (host.isEmpty()) null else row(name, (if (sending) "Sending" else "Selected · turn on tracking to send") + " · $host:$port", "router", ocui.chevron()) { editing = "saved" },
                row("New receiver", icon = "oc_icon_add_filled_24", trailing = ocui.chevron()) { editing = "" }).toTypedArray())
            val others = destinations.filter { it.host != host || it.port != port }
            Group("Other receivers", if (mode == "vrchat") "Turn on OSC in VRChat's Action Menu to see it here." else null,
                *(if (others.isEmpty()) arrayOf(row("Looking for receivers on this network…"))
                  else others.map { d -> row(d.name, "${d.host}:${d.port}", "router", ocui.button("Connect") { save(d.host, d.port, d) }) }.toTypedArray()))
        }
    }
    @Composable private fun ReceiverPage(name: String?, host: String, port: String, back: () -> Unit, save: (String, Int) -> Unit, forget: () -> Unit) {
        var address by rememberSaveable { mutableStateOf(host) }
        var number by rememberSaveable { mutableStateOf(port) }
        var problem by remember { mutableStateOf("") }
        Page(name ?: "New receiver", back) {
            Group(null, problem.ifEmpty { "Find the address in the receiving app's OSC settings." },
                row("IP address", trailing = ocui.input(address, "Receiver address", "IP address", 200, DigitsKeyListener.getInstance("0123456789.")) { address = it; problem = "" }),
                row("Port", trailing = ocui.input(number, "9000", "Port", 100, DigitsKeyListener.getInstance("0123456789")) { number = it; problem = "" }),
                row(if (name == null) "Use this receiver" else "Save changes", trailing = ocui.button("Save", "PRIMARY") {
                    val p = number.toIntOrNull(); val octets = address.trim().split('.')
                    if (p == null || p !in 1..65535 || octets.size != 4 || octets.any { (it.toIntOrNull() ?: -1) !in 0..255 })
                        problem = "Enter the receiver's IPv4 address and a port from 1 to 65535."
                    else save(address.trim(), p)
                }))
            if (name != null) Group(null, null, row("Forget this receiver", "Tracking stops sending to it.", trailing = ocui.button("Forget", "DANGER") { forget() }))
        }
    }

    private class Parameter(val name: String, val area: String, val minimum: Float, val maximum: Float, val neutral: Float, val modeled: Boolean, val partner: String?)
    private class Adjustable(val parameters: List<Parameter>, val areas: List<String>, val settings: JSONObject, val live: Map<String, FloatArray>, val sink: String)
    private fun label(name: String) = name.replace(Regex("(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=[XY]$)"), " ")
    private fun pair(p: Parameter) = if (p.partner != null || p.area == "Gaze" && p.name.contains(Regex("Left|Right")))
        label(p.name.replace(Regex("Left|Right"), "")) + ", both sides" else null

    private fun local(file: java.io.File) = Adjustable(
        parameters.map { Parameter(it, OutputAdjustments.area(it), OutputAdjustments.minimum(it), 1f, OutputAdjustments.neutral(it), OutputAdjustments.modeled(it), OutputAdjustments.partner(it)) },
        OutputAdjustments.AREAS.toList(), OutputAdjustments.read(file),
        if (System.currentTimeMillis() - OutputAdjustments.liveTime < 2000) OutputAdjustments.live else emptyMap(), "VRChat")
    private fun remote(json: String): Adjustable? = runCatching {
        val status = JSONObject(json); val list = status.getJSONArray("parameters")
        val inputs = status.optJSONObject("inputs") ?: JSONObject(); val outputs = status.optJSONObject("outputs") ?: JSONObject()
        val parameters = (0 until list.length()).map { i -> list.getJSONObject(i).run {
            Parameter(getString("name"), getString("area"), getDouble("minimum").toFloat(), getDouble("maximum").toFloat(), getDouble("neutral").toFloat(), optBoolean("modeled"), optString("partner").ifEmpty { null })
        } }
        val areas = status.optJSONArray("areas")?.let { a -> (0 until a.length()).map(a::getString) } ?: parameters.map { it.area }.distinct()
        Adjustable(parameters, areas, status.optJSONObject("settings") ?: JSONObject(),
            parameters.associate { it.name to floatArrayOf(inputs.optDouble(it.name, 0.0).toFloat(), outputs.optDouble(it.name, 0.0).toFloat()) }, "VRCFaceTracking")
    }.getOrNull()

    private fun adjustable() = if (TrackingService.outputMode(settings) == "pc") TrackingService.pcAdjustments?.let(::remote) else local(TrackingService.adjustmentsFile(this))

    @Composable private fun AdjustmentsPage() {
        val pc = TrackingService.outputMode(settings) == "pc"
        val file = remember { TrackingService.adjustmentsFile(this) }
        var data by remember { mutableStateOf(adjustable()) }
        var edited by remember { mutableStateOf<JSONObject?>(null) }
        var path by rememberSaveable { mutableStateOf(openPath.also { openPath = "" }) }
        LaunchedEffect(resumed, pc) {
            while (resumed) {
                if (pc) TrackingService.adjustmentsWanted = System.nanoTime()
                data = if (pc) TrackingService.pcAdjustments?.let(::remote) ?: data else local(file)
                delay(250)
            }
        }
        fun save(settings: JSONObject) {
            edited = settings
            if (pc) TrackingService.adjustmentsToSend = settings.toString().toByteArray()
            else runCatching { OutputAdjustments.write(file, settings) }.onFailure { notice = "Couldn't save adjustments: ${it.message}" }
        }
        val current = data
        if (current == null) {
            Page(pages[1]) {
                Group(null, "Turn on tracking with QFT+ on your PC, open VRCFaceTracking with the QFT+ module, and keep this page open.",
                    row("Connecting to QFT+ on your PC…", "Adjustments made here change what your PC sends to VRChat.", "oc_icon_computer_filled_24"))
                ConvergenceGroups()
            }
            return
        }
        val config = edited ?: current.settings
        val chosen = current.parameters.filter { it.name == path }.ifEmpty { current.parameters.filter { pair(it) == path } }
        val area = current.areas.firstOrNull { it == path } ?: chosen.firstOrNull()?.area
        when {
            path.isEmpty() -> Page(pages[1]) {
                val areas = current.areas.filter { a -> current.parameters.any { it.area == a } }
                Group(null, "Changes apply to ${current.sink} output. ${if (pc) "This edits the same settings as Adjustments in QFT+ on your PC." else "Raw OSC and QFT+ on your PC receive measured values."}",
                    row("All parameters", "Defaults for every area", "tune", ocui.chevron()) { path = "*" })
                Group("Areas", null, *areas.map { a ->
                    val members = current.parameters.filter { it.area == a }
                    val custom = (config.optJSONObject(a)?.length() ?: 0) > 0 || members.any { (config.optJSONObject(it.name)?.length() ?: 0) > 0 }
                    row(a, "${members.size} parameter${if (members.size == 1) "" else "s"}" + if (custom) " · Customized" else "", areaIcons[a], ocui.chevron()) { path = a }
                }.toTypedArray())
            }
            else -> Page(if (path == "*") "All parameters" else chosen.singleOrNull()?.let { label(it.name) } ?: path, back = { path = if (chosen.isNotEmpty()) area!! else "" }) {
                if (path == "Gaze") ConvergenceGroups()
                key(path) { Editor(current, config, path, area, chosen, ::save) }
                if (chosen.isEmpty() && area != null) Group("Parameters", null, *current.parameters.filter { it.area == area }.flatMap { p ->
                    val pair = pair(p)
                    listOfNotNull(if (pair != null && current.parameters.first { pair(it) == pair } == p && current.parameters.count { pair(it) == pair } == 2)
                        row(pair, "Change both sides together", trailing = ocui.chevron()) { path = pair } else null,
                        row(label(p.name), current.live[p.name]?.let { "${shown(p, it[0])} → ${shown(p, it[1])}" } ?: "Not sent",
                            trailing = ocui.chevron()) { path = p.name })
                }.toTypedArray())
            }
        }
    }
    private fun percent(minimum: Float) = minimum >= 0f
    private fun shown(p: Parameter, v: Float) = shown(percent(p.minimum), v)
    private fun shown(percent: Boolean, v: Float) = if (percent) String.format(Locale.getDefault(), "%.0f%%", v * 100) else String.format(Locale.getDefault(), "%.2f", v)

    @Composable private fun Editor(data: Adjustable, config: JSONObject, selection: String, area: String?, chosen: List<Parameter>, save: (JSONObject) -> Unit) {
        val parameter = chosen.firstOrNull()
        val targets = chosen.map { it.name }.ifEmpty { listOf(selection) }
        val values = remember { JSONObject((config.optJSONObject(targets[0]) ?: JSONObject()).toString()) }
        val changed = remember { mutableSetOf<String>() }
        val inherited = config.optJSONObject("*") ?: JSONObject()
        val group = if (parameter != null) config.optJSONObject(area!!) ?: JSONObject() else JSONObject()
        var version by remember { mutableIntStateOf(0) }
        var problem by remember { mutableStateOf("") }
        fun number(node: JSONObject, key: String) = node.optDouble(key, Double.NaN).takeIf { it.isFinite() }
        fun value(key: String, fallback: Float, inherit: Boolean = true): Float =
            (number(values, key) ?: number(group, key) ?: (if (inherit) number(inherited, key) else null))?.toFloat() ?: fallback
        val names = when { parameter != null -> chosen; area != null -> data.parameters.filter { it.area == area }; else -> data.parameters }
        val signed = area != null && names.isNotEmpty() && names.all { it.minimum < 0 }
        val minimum = parameter?.minimum ?: if (signed) names.minOf { it.minimum } else 0f
        val maximum = parameter?.maximum ?: if (signed) names.maxOf { it.maximum } else 1f
        val pct = percent(minimum)
        fun fmt(v: Float) = shown(pct, v)
        val smoothing = names.map { OutputAdjustments.defaultSmoothing(it.area) }.distinct().singleOrNull() ?: 0f
        LaunchedEffect(version) {
            if (version == 0) return@LaunchedEffect
            delay(250)
            val valid = value("inputMax", maximum, false) - value("inputMin", minimum, false) >= .0001f && value("outputMin", minimum, false) <= value("outputMax", maximum, false)
            problem = if (valid) "" else "Input minimum must be below input maximum, and output minimum can't exceed output maximum. Changes aren't saved until the ranges are valid."
            if (valid) save(JSONObject(config.toString()).apply { targets.forEach { t ->
                put(t, (optJSONObject(t) ?: JSONObject()).apply { changed.forEach { put(it, values.get(it)) } }) } })
        }
        fun set(key: String, v: Float) { values.put(key, v.toDouble()); changed += key; version++ }
        key(version) {
            if (problem.isNotEmpty()) Group(null, null, row(problem, icon = "oc_icon_warning_filled_24"))
            val differ = chosen.size > 1 && config.optJSONObject(targets[0])?.toString() != config.optJSONObject(targets[1])?.toString()
            if (parameter != null) Group("Live", if (differ) "The two sides have different settings. Values shown are from ${label(targets[0])}; changes apply to both." else null,
                *chosen.map { p -> row(if (chosen.size > 1) label(p.name) else "Input → output",
                    data.live[p.name]?.let { "${fmt(it[0])} → ${fmt(it[1])}" } ?: "Waiting for tracking") }.toTypedArray())
            val modeled = names.any { it.modeled }
            val paired = chosen.size != 1 && names.any { it.partner != null }
            if (modeled || paired) Group("Source", if (modeled && (area == null || area == "Pupils")) "Meta doesn't measure pupils, so passthrough holds them at 50%." else null,
                *listOfNotNull(
                    if (modeled) row("Headset passthrough", "Send Meta's own tracking instead of QFT+'s models",
                        trailing = ocui.toggle(value("passthrough", 0f) >= .5f, "Headset passthrough") { set("passthrough", if (it) 1f else 0f) }) else null,
                    if (paired) row("Match left and right", "Averaging evens out one-sided jitter; following the stronger side keeps blinks together",
                        trailing = ocui.select(listOf("Off", "Average both sides", "Follow the stronger side"), value("match", 0f).toInt().coerceIn(0, 2), "Match left and right") { set("match", it.toFloat()) }) else null
                ).toTypedArray())
            Group("Output", "Dead zone ignores movement around neutral, then scales the rest to reach full output. Offset shifts the result afterwards.",
                numberRow("Strength", value("strength", 1f), 0f..10f, { String.format(Locale.getDefault(), "%.0f%%", it * 100) }) { set("strength", it) },
                numberRow("Offset", value("offset", 0f), (minimum - maximum)..(maximum - minimum), ::fmt) { set("offset", it) },
                numberRow("Dead zone", value("deadzone", 0f), 0f..(maximum - minimum), ::fmt) { set("deadzone", it) },
                numberRow("Smoothing", value("smoothing", smoothing), 0f..100f, { "%.0f".format(it) }) { set("smoothing", it) })
            Group("Response", "A curve of 1 is linear; below 1 boosts small movements, above 1 makes them gentler.",
                numberRow("Response curve", value("curve", 1f), .1f..5f, { "%.2f".format(it) }) { set("curve", it) },
                numberRow("Smoothing when relaxing", value("release", value("smoothing", smoothing)), 0f..100f, { "%.0f".format(it) }) { set("release", it) },
                row("Invert output", trailing = ocui.toggle(value("invert", 0f) >= .5f, "Invert output") { set("invert", if (it) 1f else 0f) }))
            if (area != null) {
                val rest = names.map { it.neutral }.distinct().singleOrNull()
                Group("Limits", "Set the range you can comfortably reach. Output limits clamp the result; equal limits hold a fixed value.",
                    *listOfNotNull(
                        numberRow("Input minimum", value("inputMin", minimum, false), minimum..maximum, ::fmt) { set("inputMin", it) },
                        numberRow("Input maximum", value("inputMax", maximum, false), minimum..maximum, ::fmt) { set("inputMax", it) },
                        rest?.let { numberRow("Resting input", value("neutral", it, false), minimum..maximum, ::fmt) { v -> set("neutral", v) } },
                        numberRow("Output minimum", value("outputMin", minimum, false), minimum..maximum, ::fmt) { set("outputMin", it) },
                        numberRow("Output maximum", value("outputMax", maximum, false), minimum..maximum, ::fmt) { set("outputMax", it) }
                    ).toTypedArray())
            }
            Group(null, null, row(if (selection == "*") "Restore defaults" else "Use inherited settings",
                if (selection == "*") "Clears the defaults for every parameter" else "Removes the settings made here",
                trailing = ocui.button(if (selection == "*") "Restore" else "Remove", "DANGER") {
                    values.keys().asSequence().toList().forEach(values::remove); changed.clear()
                    save(JSONObject(config.toString()).apply { targets.forEach { remove(it) } })
                    version = 0; problem = ""
                }))
        }
    }

    override fun onResume() { super.onResume(); resumed = true; calibrationResult?.let { notice = it; calibrationResult = null } }
    override fun onNewIntent(next: Intent) { super.onNewIntent(next); setIntent(next); tab = next.getIntExtra("page", tab).coerceIn(0, pages.lastIndex) }
    override fun onSaveInstanceState(state: Bundle) { super.onSaveInstanceState(state); state.putInt("page", tab) }
    override fun onPause() { resumed = false; if (::ocui.isInitialized) ocui.dismissOverlays(); super.onPause() }
}
