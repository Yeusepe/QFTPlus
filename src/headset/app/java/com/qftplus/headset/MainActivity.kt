package com.qftplus.headset

import android.content.Intent
import android.os.Bundle
import android.os.SystemClock
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
import androidx.compose.ui.viewinterop.AndroidView
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.util.Locale

class MainActivity : ComponentActivity() {
    private val settings by lazy { getSharedPreferences("settings", 0) }
    private var resumed by mutableStateOf(false)
    private var notice by mutableStateOf("")
    private var resetting by mutableStateOf(false)
    private var setup by mutableStateOf(false)
    private val pages get() = listOf(getString(R.string.tracking), getString(R.string.adjustments), getString(R.string.calibration), getString(R.string.connections))
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
        setup = !settings.getBoolean("setupDone", settings.contains("enabled"))
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
                    text = getString(R.string.settings_ui_unavailable, e.message.orEmpty())
                    textSize = 22f
                })
                addView(android.widget.Button(this@MainActivity).apply {
                    text = getString(R.string.stop_tracking); setOnClickListener { stop(); text = getString(R.string.tracking_stopped) }
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
    private val rateNames get() = listOf(getString(R.string.fastest), getString(R.string.text_30_per_second), getString(R.string.text_20_per_second))
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
                            ocui.Dropdown(pages, tab, ::open, getString(R.string.page), Modifier.widthIn(max = 230.dp))
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
            if (setup) SetupSheet()
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
            Hit(getString(R.string.face_and_eye_tracking), null, 0, words = getString(R.string.on_off_start_stop_status)),
            Hit(getString(R.string.headset_setup), null, 0, words = getString(R.string.face_eye_hand_movement_tracking_settings)),
            Hit(getString(R.string.pupil_dilation), getString(R.string.features), 0, words = getString(R.string.eyes_pupils)),
            Hit(getString(R.string.convergence), areaTitle("Gaze"), 1, "Gaze", words = getString(R.string.independent_eye_gaze_depth_startup)),
            Hit(getString(R.string.convergence_strength), areaTitle("Gaze"), 1, "Gaze", words = getString(R.string.eyes_depth_gain_vergence)),
            Hit(getString(R.string.reset_to_100), areaTitle("Gaze"), 1, "Gaze", words = getString(R.string.convergence_strength_default)),
            Hit(getString(R.string.tongue_direction), getString(R.string.features), 0),
            Hit(getString(R.string.resume_after_restart), getString(R.string.features), 0, words = getString(R.string.boot_startup_reboot_automatically)),
            Hit(getString(R.string.update_rate), getString(R.string.performance), 0, words = getString(R.string.fps_frame_rate_speed_latency_smooth_battery_heat)),
            Hit(getString(R.string.face_and_tongue), null, 2, words = getString(R.string.calibrate_mouth_avatar_expressions)),
            Hit(getString(R.string.pupil_dilation), null, 2, words = getString(R.string.calibrate_eyes_pupils)),
            Hit(getString(R.string.avatar_distance), getString(R.string.calibration_space), 2),
            Hit(getString(R.string.reduce_motion), getString(R.string.calibration_space), 2, words = getString(R.string.animation)),
            Hit(getString(R.string.remove_saved_calibration), null, 2, words = getString(R.string.delete_reset_clear)),
            Hit("VRChat", getString(R.string.send_tracking_to), 3, words = getString(R.string.output_destination_osc_vrcft)),
            Hit(getString(R.string.osc_apps), getString(R.string.send_tracking_to), 3, words = getString(R.string.output_destination_raw)),
            Hit(getString(R.string.qft_on_your_pc), getString(R.string.send_tracking_to), 3, words = getString(R.string.output_destination_pair_computer_vrcfacetracking_module)))
        if (TrackingService.outputMode(settings) != "pc") hits += listOf(
            Hit(getString(R.string.new_receiver), getString(R.string.my_receiver), 3, words = getString(R.string.add_manual_ip_address_port_host)),
            Hit(getString(R.string.other_receivers), null, 3, row = getString(R.string.looking_for_receivers_on_this_network), words = getString(R.string.find_search_discover_network_osc_connect)))
        adjustable()?.let { data ->
            hits += Hit(getString(R.string.all_parameters), null, 1, "*", null, getString(R.string.defaults_every))
            data.areas.filter { a -> data.parameters.any { it.area == a } }.forEach { hits += Hit(areaTitle(it), getString(R.string.areas), 1, it, null) }
            data.parameters.forEach { hits += Hit(label(it.name), areaTitle(it.area), 1, it.name, null, it.name) }
            data.parameters.mapNotNull { p -> pair(p)?.let { it to p.area } }.distinct().forEach { (pair, area) -> hits += Hit(pairTitle(pair), areaTitle(area), 1, pair, null, getString(R.string.together_left_right_eyes)) }
            listOf(getString(R.string.headset_passthrough) to getString(R.string.source_meta_native), getString(R.string.match_left_and_right) to getString(R.string.source_average_stronger_side), getString(R.string.strength) to getString(R.string.output_gain_multiplier),
                getString(R.string.offset) to getString(R.string.output_shift), getString(R.string.dead_zone) to getString(R.string.output_deadzone), getString(R.string.smoothing) to getString(R.string.output_filter_jitter), getString(R.string.response_curve) to getString(R.string.response_gamma),
                getString(R.string.smoothing_when_relaxing) to getString(R.string.response_release), getString(R.string.invert_output) to getString(R.string.response_reverse), getString(R.string.restore_defaults) to getString(R.string.reset))
                .forEach { (title, words) -> hits += Hit(title, getString(R.string.all_parameters), 1, "*", title, words) }
            listOf(getString(R.string.input_minimum), getString(R.string.input_maximum), getString(R.string.resting_input), getString(R.string.output_minimum), getString(R.string.output_maximum))
                .forEach { hits += Hit(it, getString(R.string.limits_in_each_area_and_parameter), 1, "", null, getString(R.string.range_clamp_neutral)) }
        }
        return hits
    }
    private fun search(text: String): List<Hit> {
        val phrase = text.trim(); val terms = phrase.split(Regex("\\s+")).filter { it.isNotEmpty() }
        if (terms.isEmpty()) return emptyList()
        return index().mapNotNull { hit ->
            val title = hit.title
            val all = "$title ${hit.where.orEmpty()} ${pages[hit.tab]} ${hit.words}"
            if (terms.any { !all.contains(it, ignoreCase = true) }) null
            else hit to when { title.startsWith(phrase, ignoreCase = true) -> 0; title.split(' ').any { it.startsWith(terms[0], ignoreCase = true) } -> 1; terms.all { title.contains(it, ignoreCase = true) } -> 2; else -> 3 }
        }.sortedBy { it.second }.map { it.first }
    }
    private fun go(hit: Hit) { tab = hit.tab; openPath = hit.path; focus = hit.row; root++; query = ""; notice = "" }
    @Composable private fun Search(modifier: Modifier) = ocui.SearchField(query, { query = it }, { search(query).firstOrNull()?.let(::go) }, getString(R.string.search_qft), modifier)

    @Composable private fun SearchPage() {
        val hits = remember(query) { search(query) }
        Page(getString(R.string.search_results)) {
            if (hits.isEmpty()) Group(null, getString(R.string.check_the_spelling_or_search_for_a_setting), row(getString(R.string.no_search_results, query.trim())))
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
        Group(null, getString(R.string.turns_on_the_headset_s_face_and_eye),
            row(getString(R.string.face_and_eye_tracking), if (enabled) status else TrackingService.error?.let { getString(R.string.stopped_reason, it) } ?: getString(R.string.off), trailing = ocui.toggle(enabled, getString(R.string.face_and_eye_tracking)) {
                if (it) start() else stop(); enabled = settings.getBoolean("enabled", false)
            }))
        Group(getString(R.string.features), null,
            switchRow(getString(R.string.pupil_dilation), getString(R.string.uses_your_pupil_calibration), pupils, "pupils"),
            switchRow(getString(R.string.tongue_direction), getString(R.string.uses_your_face_calibration), tongue, "tongue"),
            switchRow(getString(R.string.resume_after_restart), getString(R.string.turns_tracking_back_on_if_it_was_on), boot, "boot"))
        Group(getString(R.string.performance), getString(R.string.how_often_your_face_is_measured_and_sent),
            row(getString(R.string.update_rate), trailing = ocui.select(rateNames, rates.indexOf(rate).coerceAtLeast(0), getString(R.string.update_rate)) { i ->
                rate = rates[i]; settings.edit().putInt("rate", rate).apply(); restartTracking()
            }))
        Group(null, null, row(getString(R.string.headset_setup), getString(R.string.shows_which_headset_settings_qft_needs), "oc_icon_settings_filled_24", ocui.chevron()) { setupReview = true; setup = true })
    }

    private class Need(val key: String, val place: String?, val icon: String, val title: Int, val about: Int)
    private val needs = listOf(
        Need("root", null, "oc_icon_lock_filled_24", R.string.allow_qft_in_magisk, R.string.allow_qft_in_magisk_about),
        Need("ft_enabled", "face_tracking_turn_on", "oc_icon_face_tracking_on_filled_24", R.string.turn_on_natural_facial_expressions, R.string.setup_face_about),
        Need("et_enabled", "eye_tracking_turn_on", "oc_icon_eye_tracking_on_filled_24", R.string.turn_on_eye_tracking, R.string.setup_eye_about))
    private var setupReview = false
    private fun completeSetup() { settings.edit().putBoolean("setupDone", true).apply(); setup = false; setupReview = false }

    private class Spring(val response: Float, val damping: Float, val settle: Float) : android.animation.TimeInterpolator {
        override fun getInterpolation(t: Float): Float {
            if (t >= 1f) return 1f
            val omega = 2 * Math.PI / response; val damped = omega * Math.sqrt(1.0 - damping * damping); val time = t * settle
            return (1 - Math.exp(-damping * omega * time) * (Math.cos(damped * time) + damping * omega / damped * Math.sin(damped * time))).toFloat()
        }
        val millis get() = (settle * 1000).toLong()
    }
    private val move = Spring(.6f, .82f, .77f)
    private val fade = Spring(.9f, .92f, .91f)

    @Composable private fun SetupSheet() {
        var attempt by remember { mutableIntStateOf(0) }
        var known by remember { mutableStateOf<Map<String, Boolean>?>(null) }
        LaunchedEffect(resumed, attempt) {
            if (resumed) withContext(Dispatchers.IO) { runCatching {
                // One root shell for the whole sheet, so Magisk grants once instead of on every check.
                val shell = ProcessBuilder("su").redirectErrorStream(true).start()
                try {
                    val input = shell.inputStream.bufferedReader(); val output = shell.outputStream.bufferedWriter()
                    while (isActive) {
                        output.write("for k in ft_enabled et_enabled; do oculuspreferences --get \$k; done; echo QFT_DONE\n"); output.flush()
                        val text = generateSequence(input::readLine).takeWhile { it != "QFT_DONE" }.joinToString("\n")
                        val read = Regex("""\[(\w+) : (\w+)]""").findAll(text).associate { it.groupValues[1] to (it.groupValues[2] in setOf("true", "1")) }
                        known = read + ("root" to read.isNotEmpty())
                        delay(1000)
                    }
                } finally { shell.destroy() }
            }.onFailure { if (it !is kotlinx.coroutines.CancellationException) known = mapOf("root" to false) } }
        }
        val state = known ?: return
        val missing = needs.firstOrNull { state[it.key] != true }
        val needed = remember { missing != null }
        if (!needed && !setupReview) { LaunchedEffect(Unit) { completeSetup() }; return }
        val frame = remember { arrayOfNulls<android.view.View>(1) }
        fun close() {
            val view = frame[0] ?: return completeSetup()
            view.animate().alpha(0f).setDuration(220).setInterpolator(android.view.animation.AccelerateInterpolator()).withEndAction { completeSetup() }
            (view as ViewGroup).getChildAt(0).animate().scaleX(.96f).scaleY(.96f).translationY(16 * resources.displayMetrics.density).setDuration(220).setInterpolator(android.view.animation.AccelerateInterpolator())
        }
        BackHandler { close() }
        val density = resources.displayMetrics.density
        AndroidView(modifier = Modifier.fillMaxSize(), factory = { context ->
            android.widget.FrameLayout(context).apply {
                frame[0] = this
                setBackgroundColor(0xCC000000.toInt()); isClickable = true
                val sheet = ocui.sheet()
                addView(sheet, android.widget.FrameLayout.LayoutParams(ocui.sheetWidth, -2, android.view.Gravity.CENTER))
                ocui.hero(sheet, bubbles(context), ocui.sheetWidth / (158 * density))
                alpha = 0f
                animate().alpha(1f).setDuration(fade.millis).setInterpolator(fade)
                sheet.scaleX = .94f; sheet.scaleY = .94f; sheet.translationY = 28 * density
                sheet.animate().scaleX(1f).scaleY(1f).translationY(0f).setDuration(move.millis).setInterpolator(move)
            }
        }, update = { view ->
            val sheet = (view as ViewGroup).getChildAt(0)
            val key = missing?.key ?: "ready"
            if (sheet.tag != null && sheet.tag != key) for (name in listOf("title", "body_text", "primary_button"))
                ocui.sheetPart(sheet, name).apply { alpha = 0f; animate().alpha(1f).setDuration(fade.millis).setInterpolator(fade) }
            sheet.tag = key
            showBubbles(sheet.findViewWithTag("bubbles"), state, missing)
            ocui.sheetText(sheet, "title", getString(missing?.title ?: R.string.headset_ready))
            ocui.sheetText(sheet, "body_text", getString(missing?.about ?: R.string.headset_ready_about))
            ocui.sheetButton(sheet, "primary_button", getString(when { missing == null -> R.string.done; missing.place == null -> R.string.try_again; else -> R.string.open_settings })) {
                when { missing == null -> close(); missing.place == null -> attempt++; else -> openMovementTracking(missing.place) }
            }
            ocui.sheetButton(sheet, "secondary_button", if (missing == null) null else getString(R.string.not_now)) { close() }
        })
    }

    private fun bubbles(context: android.content.Context) = android.widget.FrameLayout(context).apply {
        tag = "bubbles"
        val density = resources.displayMetrics.density
        val width = ocui.sheetWidth.toFloat(); val height = 158 * density
        // Discs circle-packed with a 4 dp gap (each frame adds a 3 dp ring inset), centred as a group.
        listOf(Triple(14f, 46f, 64), Triple(-35f, -12f, 92), Triple(45f, -12f, 72)).forEachIndexed { i, (x, y, size) ->
            val side = (size * density).toInt()
            addView(android.widget.FrameLayout(context).apply {
                addView(android.view.View(context).apply { background = android.graphics.drawable.GradientDrawable().apply { shape = android.graphics.drawable.GradientDrawable.OVAL; setStroke((1.5f * density).toInt().coerceAtLeast(1), 0xFFFFFFFF.toInt()) }; alpha = 0f },
                    android.widget.FrameLayout.LayoutParams(-1, -1))
                addView(android.view.View(context).apply { background = android.graphics.drawable.GradientDrawable().apply { shape = android.graphics.drawable.GradientDrawable.OVAL } },
                    android.widget.FrameLayout.LayoutParams(-1, -1).apply { setMargins((3 * density).toInt(), (3 * density).toInt(), (3 * density).toInt(), (3 * density).toInt()) })
                addView(android.widget.ImageView(context).apply { setImageDrawable(ocui.icon(needs[i].icon).mutate()); imageTintList = android.content.res.ColorStateList.valueOf(0xFFFFFFFF.toInt()) },
                    android.widget.FrameLayout.LayoutParams(side * 2 / 5, side * 2 / 5, android.view.Gravity.CENTER))
                importantForAccessibility = android.view.View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }, android.widget.FrameLayout.LayoutParams(side, side).apply { leftMargin = (width / 2 + x * density - side / 2).toInt(); topMargin = (height / 2 + y * density - side / 2).toInt() })
        }
    }

    private fun showBubbles(cluster: ViewGroup, state: Map<String, Boolean>, missing: Need?) {
        for (i in needs.indices) {
            val bubble = cluster.getChildAt(i) as ViewGroup
            val on = state[needs[i].key] == true
            val disc = bubble.getChildAt(1); val ring = bubble.getChildAt(0)
            if (bubble.tag != on) {
                if (on && bubble.tag != null) { bubble.scaleX = .8f; bubble.scaleY = .8f; bubble.animate().scaleX(1f).scaleY(1f).setDuration(move.millis).setInterpolator(move) }
                bubble.tag = on
                (disc.background as android.graphics.drawable.GradientDrawable).setColor(if (on) 0xFFFFFFFF.toInt() else 0x26FFFFFF)
                (bubble.getChildAt(2) as android.widget.ImageView).imageTintList = android.content.res.ColorStateList.valueOf(if (on) 0xFF1C1E21.toInt() else 0xFFFFFFFF.toInt())
            }
            val attention = needs[i] == missing
            if (attention && ring.animation == null && ring.tag == null) {
                ring.tag = android.animation.ObjectAnimator.ofFloat(ring, "alpha", .15f, .7f).apply {
                    duration = 1100; repeatMode = android.animation.ValueAnimator.REVERSE; repeatCount = android.animation.ValueAnimator.INFINITE; start()
                }
            } else if (!attention) { (ring.tag as? android.animation.Animator)?.cancel(); ring.tag = null; ring.alpha = 0f }
        }
    }

    private fun openMovementTracking(setting: String) {
        try {
            startActivity(Intent("com.oculus.action.SETTINGS").setPackage(Ocui.PROVIDER).putExtra("uri", "/movement_tracking?setting=$setting")
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK))
        } catch (e: Exception) { notice = getString(R.string.open_settings_failed, e.message.orEmpty()) }
    }

    @Composable private fun ConvergenceGroups() {
        var convergence by remember { mutableStateOf(settings.getBoolean("convergence", true)) }
        var vergenceGain by remember { mutableFloatStateOf(settings.getFloat("vergenceGain", 1f)) }
        LaunchedEffect(resumed) { while (resumed) {
            convergence = settings.getBoolean("convergence", true); vergenceGain = settings.getFloat("vergenceGain", 1f); delay(500)
        } }
        Group(getString(R.string.convergence), null,
            row(getString(R.string.convergence), getString(R.string.tracks_each_eye_separately_for_depth_starts_with), trailing = ocui.toggle(convergence, getString(R.string.convergence)) {
                convergence = it; settings.edit().putBoolean("convergence", it).apply(); restartTracking()
            }))
        if (convergence) Group(null, getString(R.string.text_100_uses_the_calibrated_movement_at_0_both),
            numberRow(getString(R.string.convergence_strength), vergenceGain, 0f..3f, { "${(it * 100).toInt()}%" }) {
                vergenceGain = it; settings.edit().putFloat("vergenceGain", it).apply()
            },
            row(getString(R.string.reset_to_100), trailing = ocui.button(getString(R.string.reset_2)) { vergenceGain = 1f; settings.edit().putFloat("vergenceGain", 1f).apply() }))
    }

    @Composable private fun CalibrationPage() {
        var confirm by remember { mutableStateOf(false) }
        var distance by remember { mutableFloatStateOf(settings.getFloat("guideDistance", .75f).coerceIn(.5f, 1.2f)) }
        val reduceMotion = preference("reduceMotion")
        Group(null, getString(R.string.calibrate_after_changing_how_the_headset_fits_your),
            row(getString(R.string.face_and_tongue), getString(R.string.copy_12_expressions_from_a_meta_avatar_about), "oc_icon_face_tracking_on_filled_24",
                ocui.button(getString(R.string.calibrate), "PRIMARY", !resetting) { begin(false) }),
            row(getString(R.string.pupil_dilation), getString(R.string.follow_a_dot_as_the_space_darkens_and), "adjust",
                ocui.button(getString(R.string.calibrate), enabled = !resetting) { begin(true) }))
        Group(getString(R.string.calibration_space), getString(R.string.each_calibration_shows_a_different_meta_preset_avatar),
            numberRow(getString(R.string.avatar_distance), distance, .5f..1.2f, { getString(R.string.distance_metres, it) }) {
                distance = it; settings.edit().putFloat("guideDistance", it).apply()
            },
            switchRow(getString(R.string.reduce_motion), getString(R.string.shows_each_expression_without_animating_into_it), reduceMotion, "reduceMotion"))
        Group(null, null, row(getString(R.string.remove_saved_calibration), getString(R.string.stops_tracking_and_removes_your_face_and_pupil),
            trailing = ocui.button(getString(R.string.remove), "DANGER", !resetting) { confirm = true }))
        if (confirm) ocui.Dialog(getString(R.string.remove_calibration),
            getString(R.string.this_removes_your_saved_face_and_pupil_calibration), getString(R.string.remove), { confirm = false }) {
            confirm = false
            if (!resetting) {
                resetting = true; stop()
                Thread {
                    try { TrackingService.removeCalibration(this); runOnUiThread { notice = getString(R.string.saved_calibration_removed) } }
                    catch (e: Exception) { runOnUiThread { notice = getString(R.string.remove_calibration_failed, e.message.orEmpty()) } }
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
        LaunchedEffect(resumed) { while (resumed) { sending = settings.getBoolean("enabled", false) && SystemClock.elapsedRealtime() - TrackingService.lastFrame < 2000 && TrackingService.latest != null; delay(1000) } }
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
        fun choose(id: String) { if (mode != id) { stop(); mode = id; settings.edit().putString("output", id).apply(); notice = getString(R.string.destination_changed_turn_tracking_on_when_you_re) } }
        val paired = !settings.getString("key", "").isNullOrEmpty()
        Page(pages[3]) {
            Group(getString(R.string.send_tracking_to), null,
                row("VRChat", getString(R.string.standard_vrcft_v2_parameters_over_osc), "sports_esports", trailing = ocui.radio(mode == "vrchat", "VRChat") { choose("vrchat") }) { choose("vrchat") },
                row(getString(R.string.osc_apps), getString(R.string.named_qft_values), "apps", trailing = ocui.radio(mode == "raw", getString(R.string.osc_apps)) { choose("raw") }) { choose("raw") },
                row(getString(R.string.qft_on_your_pc), if (paired) getString(R.string.paired_connects_automatically) else getString(R.string.pair_from_settings_in_qft_on_your_pc), "oc_icon_computer_filled_24",
                    trailing = ocui.radio(mode == "pc", getString(R.string.qft_on_your_pc)) { choose("pc") }) { choose("pc") })
            if (mode == "pc") return@Page
            Group(getString(R.string.my_receiver), null, *listOfNotNull(
                if (host.isEmpty()) null else row(name, getString(if (sending) R.string.sending_to else R.string.selected_receiver, "$host:$port"), "router", ocui.chevron()) { editing = "saved" },
                row(getString(R.string.new_receiver), icon = "oc_icon_add_filled_24", trailing = ocui.chevron()) { editing = "" }).toTypedArray())
            val others = destinations.filter { it.host != host || it.port != port }
            Group(getString(R.string.other_receivers), if (mode == "vrchat") getString(R.string.turn_on_osc_in_vrchat_s_action_menu) else null,
                *(if (others.isEmpty()) arrayOf(row(getString(R.string.looking_for_receivers_on_this_network)))
                  else others.map { d -> row(d.name, "${d.host}:${d.port}", "router", ocui.button(getString(R.string.connect)) { save(d.host, d.port, d) }) }.toTypedArray()))
        }
    }
    @Composable private fun ReceiverPage(name: String?, host: String, port: String, back: () -> Unit, save: (String, Int) -> Unit, forget: () -> Unit) {
        var address by rememberSaveable { mutableStateOf(host) }
        var number by rememberSaveable { mutableStateOf(port) }
        var problem by remember { mutableStateOf("") }
        Page(name ?: getString(R.string.new_receiver), back) {
            Group(null, problem.ifEmpty { getString(R.string.find_the_address_in_the_receiving_app_s) },
                row(getString(R.string.ip_address), trailing = ocui.input(address, getString(R.string.receiver_address), getString(R.string.ip_address), 200, DigitsKeyListener.getInstance("0123456789.")) { address = it; problem = "" }),
                row(getString(R.string.port), trailing = ocui.input(number, "9000", getString(R.string.port), 100, DigitsKeyListener.getInstance("0123456789")) { number = it; problem = "" }),
                row(if (name == null) getString(R.string.use_this_receiver) else getString(R.string.save_changes), trailing = ocui.button(getString(R.string.save), "PRIMARY") {
                    val p = number.toIntOrNull(); val octets = address.trim().split('.')
                    if (p == null || p !in 1..65535 || octets.size != 4 || octets.any { (it.toIntOrNull() ?: -1) !in 0..255 })
                        problem = getString(R.string.enter_the_receiver_s_ipv4_address_and_a)
                    else save(address.trim(), p)
                }))
            if (name != null) Group(null, null, row(getString(R.string.forget_this_receiver), getString(R.string.tracking_stops_sending_to_it), trailing = ocui.button(getString(R.string.forget), "DANGER") { forget() }))
        }
    }

    private class Parameter(val name: String, val area: String, val minimum: Float, val maximum: Float, val neutral: Float, val modeled: Boolean, val partner: String?)
    private class Adjustable(val parameters: List<Parameter>, val areas: List<String>, val settings: JSONObject, val live: Map<String, FloatArray>, val sink: String)
    private fun label(name: String): String {
        val id = resources.getIdentifier("parameter_" + name.lowercase(Locale.ROOT), "string", packageName)
        return if (id != 0) getString(id) else name
    }
    private fun pairTitle(id: String) = getString(R.string.both_sides, label(id.removeSuffix(":both")))
    private fun areaTitle(id: String) = when (id) {
        "Brows" -> getString(R.string.brows)
        "Eyelids" -> getString(R.string.eyelids)
        "Gaze" -> getString(R.string.gaze)
        "Pupils" -> getString(R.string.pupils)
        "Nose" -> getString(R.string.nose)
        "Cheeks" -> getString(R.string.cheeks)
        "Lips" -> getString(R.string.lips)
        "Mouth" -> getString(R.string.mouth)
        "Jaw" -> getString(R.string.jaw)
        "Tongue" -> getString(R.string.tongue)
        "Throat and neck" -> getString(R.string.throat_and_neck)
        else -> id
    }
    private fun pair(p: Parameter) = if (p.partner != null || p.area == "Gaze" && p.name.contains(Regex("Left|Right")))
        p.name.replace(Regex("Left|Right"), "") + ":both" else null

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
            else runCatching { OutputAdjustments.write(file, settings) }.onFailure { notice = getString(R.string.save_adjustments_failed, it.message.orEmpty()) }
        }
        val current = data
        if (current == null) {
            Page(pages[1]) {
                Group(null, getString(R.string.turn_on_tracking_with_qft_on_your_pc),
                    row(getString(R.string.connecting_to_qft_on_your_pc), getString(R.string.adjustments_made_here_change_what_your_pc_sends), "oc_icon_computer_filled_24"))
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
                Group(null, getString(if (pc) R.string.pc_adjustment_scope else R.string.headset_adjustment_scope, current.sink),
                    row(getString(R.string.all_parameters), getString(R.string.defaults_for_every_area), "tune", ocui.chevron()) { path = "*" })
                Group(getString(R.string.areas), null, *areas.map { a ->
                    val members = current.parameters.filter { it.area == a }
                    val custom = (config.optJSONObject(a)?.length() ?: 0) > 0 || members.any { (config.optJSONObject(it.name)?.length() ?: 0) > 0 }
                    row(areaTitle(a), getString(if (custom) R.string.customized_parameter_count else R.string.parameter_count, members.size), areaIcons[a], ocui.chevron()) { path = a }
                }.toTypedArray())
            }
            else -> Page(if (path == "*") getString(R.string.all_parameters) else chosen.singleOrNull()?.let { label(it.name) } ?: if (path.endsWith(":both")) pairTitle(path) else areaTitle(path), back = { path = if (chosen.isNotEmpty()) area!! else "" }) {
                if (path == "Gaze") ConvergenceGroups()
                key(path) { Editor(current, config, path, area, chosen, ::save) }
                if (chosen.isEmpty() && area != null) Group(getString(R.string.parameters), null, *current.parameters.filter { it.area == area }.flatMap { p ->
                    val pair = pair(p)
                    listOfNotNull(if (pair != null && current.parameters.first { pair(it) == pair } == p && current.parameters.count { pair(it) == pair } == 2)
                        row(pairTitle(pair), getString(R.string.change_both_sides_together), trailing = ocui.chevron()) { path = pair } else null,
                        row(label(p.name), current.live[p.name]?.let { "${shown(p, it[0])} → ${shown(p, it[1])}" } ?: getString(R.string.not_sent),
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
            problem = if (valid) "" else getString(R.string.input_minimum_must_be_below_input_maximum_and)
            if (valid) save(JSONObject(config.toString()).apply { targets.forEach { t ->
                put(t, (optJSONObject(t) ?: JSONObject()).apply { changed.forEach { put(it, values.get(it)) } }) } })
        }
        fun set(key: String, v: Float) { values.put(key, v.toDouble()); changed += key; version++ }
        key(version) {
            if (problem.isNotEmpty()) Group(null, null, row(problem, icon = "oc_icon_warning_filled_24"))
            val differ = chosen.size > 1 && config.optJSONObject(targets[0])?.toString() != config.optJSONObject(targets[1])?.toString()
            if (parameter != null) Group(getString(R.string.live), if (differ) getString(R.string.paired_settings_differ, label(targets[0])) else null,
                *chosen.map { p -> row(if (chosen.size > 1) label(p.name) else getString(R.string.input_output),
                    data.live[p.name]?.let { "${fmt(it[0])} → ${fmt(it[1])}" } ?: getString(R.string.waiting_for_tracking)) }.toTypedArray())
            val modeled = names.any { it.modeled }
            val paired = chosen.size != 1 && names.any { it.partner != null }
            if (modeled || paired) Group(getString(R.string.source), if (modeled && (area == null || area == "Pupils")) getString(R.string.meta_doesn_t_measure_pupils_so_passthrough_holds) else null,
                *listOfNotNull(
                    if (modeled) row(getString(R.string.headset_passthrough), getString(R.string.send_meta_s_own_tracking_instead_of_qft),
                        trailing = ocui.toggle(value("passthrough", 0f) >= .5f, getString(R.string.headset_passthrough)) { set("passthrough", if (it) 1f else 0f) }) else null,
                    if (paired) row(getString(R.string.match_left_and_right), getString(R.string.averaging_evens_out_one_sided_jitter_following_the),
                        trailing = ocui.select(listOf(getString(R.string.off), getString(R.string.average_both_sides), getString(R.string.follow_the_stronger_side)), value("match", 0f).toInt().coerceIn(0, 2), getString(R.string.match_left_and_right)) { set("match", it.toFloat()) }) else null
                ).toTypedArray())
            Group(getString(R.string.output), getString(R.string.dead_zone_ignores_movement_around_neutral_then_scales),
                numberRow(getString(R.string.strength), value("strength", 1f), 0f..10f, { String.format(Locale.getDefault(), "%.0f%%", it * 100) }) { set("strength", it) },
                numberRow(getString(R.string.offset), value("offset", 0f), (minimum - maximum)..(maximum - minimum), ::fmt) { set("offset", it) },
                numberRow(getString(R.string.dead_zone), value("deadzone", 0f), 0f..(maximum - minimum), ::fmt) { set("deadzone", it) },
                numberRow(getString(R.string.smoothing), value("smoothing", smoothing), 0f..100f, { "%.0f".format(it) }) { set("smoothing", it) })
            Group(getString(R.string.response), getString(R.string.a_curve_of_1_is_linear_below_1),
                numberRow(getString(R.string.response_curve), value("curve", 1f), .1f..5f, { "%.2f".format(it) }) { set("curve", it) },
                numberRow(getString(R.string.smoothing_when_relaxing), value("release", value("smoothing", smoothing)), 0f..100f, { "%.0f".format(it) }) { set("release", it) },
                row(getString(R.string.invert_output), trailing = ocui.toggle(value("invert", 0f) >= .5f, getString(R.string.invert_output)) { set("invert", if (it) 1f else 0f) }))
            if (area != null) {
                val rest = names.map { it.neutral }.distinct().singleOrNull()
                Group(getString(R.string.limits), getString(R.string.set_the_range_you_can_comfortably_reach_output),
                    *listOfNotNull(
                        numberRow(getString(R.string.input_minimum), value("inputMin", minimum, false), minimum..maximum, ::fmt) { set("inputMin", it) },
                        numberRow(getString(R.string.input_maximum), value("inputMax", maximum, false), minimum..maximum, ::fmt) { set("inputMax", it) },
                        rest?.let { numberRow(getString(R.string.resting_input), value("neutral", it, false), minimum..maximum, ::fmt) { v -> set("neutral", v) } },
                        numberRow(getString(R.string.output_minimum), value("outputMin", minimum, false), minimum..maximum, ::fmt) { set("outputMin", it) },
                        numberRow(getString(R.string.output_maximum), value("outputMax", maximum, false), minimum..maximum, ::fmt) { set("outputMax", it) }
                    ).toTypedArray())
            }
            Group(null, null, row(if (selection == "*") getString(R.string.restore_defaults) else getString(R.string.use_inherited_settings),
                if (selection == "*") getString(R.string.clears_the_defaults_for_every_parameter) else getString(R.string.removes_the_settings_made_here),
                trailing = ocui.button(if (selection == "*") getString(R.string.restore) else getString(R.string.remove), "DANGER") {
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
