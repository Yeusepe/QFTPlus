package com.qftplus.headset

import android.app.PendingIntent
import android.app.Presentation
import android.content.Intent
import android.graphics.drawable.ColorDrawable
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.InputDevice
import android.view.ViewGroup
import android.view.MotionEvent
import android.view.Surface
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.unit.dp
import androidx.lifecycle.setViewTreeLifecycleOwner
import androidx.savedstate.setViewTreeSavedStateRegistryOwner
import java.io.File
import kotlin.math.ceil
import kotlin.math.min

internal fun startCalibration(context: android.content.Context, pupils: Boolean): String? {
    if (TrackingService.calibrating) return context.getString(R.string.calibration_is_already_running_in_the_headset)
    val task = returnTask() ?: return context.getString(R.string.root_needed)
    context.startActivity(Intent(context, CalibrationActivity::class.java).setAction(Intent.ACTION_MAIN)
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK).putExtra("pupils", pupils).putExtra("returnTask", task))
    TrackingService.lastCalibration = ""
    return null
}

@Volatile internal var calibrationResult: String? = null

private fun returnTask(): Int? = runCatching {
    val process = ProcessBuilder("su", "-c", "id -u; dumpsys activity activities | grep -m1 'ResumedActivity: ActivityRecord'")
        .redirectErrorStream(true).start()
    val dump = try {
        if (!process.waitFor(2, java.util.concurrent.TimeUnit.SECONDS)) return -1
        process.inputStream.bufferedReader().use { it.readText() }
    } finally { process.destroy() }
    if (dump.lineSequence().none { it.trim() == "0" }) return null
    val resumed = Regex("""ResumedActivity: ActivityRecord\{\S+ u0 ([^/\s]+)/\S+ t(\d+)""").find(dump) ?: return -1
    val app = resumed.groupValues[1]
    if (app.startsWith("com.oculus.") || app == "com.qftplus.headset") -1 else resumed.groupValues[2].toInt()
}.getOrNull()

class CalibrationActivity : ComponentActivity() {
    private external fun runScene(library: String, avatarFolder: String, preset: ByteArray?, scene: FloatArray, pupils: Boolean, space: Array<SetupSpace.Draw>?): String
    private external fun stopScene()
    companion object { init { System.loadLibrary("qft_calibration_scene") } }

    private lateinit var calibration: Calibration
    private lateinit var ocui: Ocui
    @Volatile private var resumed = false
    private var renderer: Thread? = null
    private var opened = 0L; private var completed = 0L; private var unfocused = 0L
    private var focusedOnce = false; @Volatile private var visible = false; private var armed by mutableStateOf(false)
    private var distance = .75f; private var reduceMotion = false
    private var lastAssessment = ""
    private val scene = FloatArray(8)
    private var display: VirtualDisplay? = null; private var presentation: Presentation? = null

    private var prompt by mutableStateOf(""); private var elapsed by mutableDoubleStateOf(0.0)
    private var done by mutableStateOf(false); private var failed by mutableStateOf(false); private var started by mutableStateOf(false)
    private var retrying by mutableStateOf(false); private var stimulus by mutableFloatStateOf(0f)
    private var avatar by mutableIntStateOf(0)
    private var cursor by mutableStateOf<Offset?>(null)
    private var pointerDown = false; private var downTime = 0L

    override fun onCreate(saved: Bundle?) {
        super.onCreate(saved)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val preferences = getSharedPreferences("settings", 0)
        distance = preferences.getFloat("guideDistance", .75f).coerceIn(.5f, 1.2f)
        reduceMotion = preferences.getBoolean("reduceMotion", false)
        if (checkSelfPermission("com.oculus.permission.FACE_TRACKING") != 0 || checkSelfPermission("com.oculus.permission.EYE_TRACKING") != 0)
            requestPermissions(arrayOf("com.oculus.permission.FACE_TRACKING", "com.oculus.permission.EYE_TRACKING"), 2)
    }
    override fun onResume() {
        super.onResume(); resumed = true
        if (renderer == null && checkSelfPermission("com.oculus.permission.FACE_TRACKING") == 0 && checkSelfPermission("com.oculus.permission.EYE_TRACKING") == 0) launch()
    }
    override fun onRequestPermissionsResult(request: Int, permissions: Array<String>, results: IntArray) {
        super.onRequestPermissionsResult(request, permissions, results)
        if (request != 2) return
        if (results.size != 2 || results[0] != 0 || results[1] != 0) { returnToPanel(getString(R.string.face_and_eye_permissions_are_needed_for_calibration)); return }
        if (resumed && renderer == null) launch()
    }
    private fun launch() {
        calibration = Calibration(this, intent.getBooleanExtra("pupils", false))
        TrackingService.calibration = calibration; TrackingService.calibrating = true
        startForegroundService(Intent(this, TrackingService::class.java))
        opened = SystemClock.elapsedRealtime()
        val pupils = calibration.isPupils
        renderer = Thread({
            val error = try {
                val folder = File(filesDir, "avatar").apply { mkdirs() }
                val preset = if (pupils) null else runCatching { AvatarAssets.prepare(this, true).readBytes() }
                    .onFailure { Log.w("QFTCalibration", "Avatar unavailable", it) }.getOrNull()
                val space = if (pupils) null else runCatching { SetupSpace.read().toTypedArray() }.onFailure { Log.w("QFTCalibration", "Setup space unavailable", it) }.getOrNull()
                if (!pupils && preset == null) avatar = 2
                runScene(applicationInfo.nativeLibraryDir + "/libovravatar2.so", folder.path, preset, scene, pupils, space)
            } catch (e: Exception) { e.message ?: e.toString() }
            val fitting = SystemClock.elapsedRealtime() + 90000
            while (calibration.done && !calibration.cancelled() && TrackingService.calibration === calibration && TrackingService.running
                && SystemClock.elapsedRealtime() < fitting) Thread.sleep(100)
            if (!calibration.done || TrackingService.calibration === calibration)
                {
                if (error.isNotEmpty()) Log.e("QFTCalibration", error)
                calibration.fail(getString(if (error.isEmpty()) R.string.calibration_cancelled_previous_calibration_retained else R.string.calibration_scene_failed))
            }
            if (TrackingService.calibration === calibration) TrackingService.calibration = null
            Log.i("QFTCalibration", calibration.message)
            TrackingService.lastCalibration = calibration.message; TrackingService.calibrating = false
            if (!getSharedPreferences("settings", 0).getBoolean("enabled", false)) stopService(Intent(this, TrackingService::class.java))
            runOnUiThread { closePanel(); if (resumed) returnToPanel(calibration.message) else finishAndRemoveTask() }
        }, "qft-calibration-scene").also { it.start() }
    }

    @Suppress("unused") private fun inputLabel(index: Int): String = getString(intArrayOf(
        R.string.calibration, R.string.calibration_input_leave, R.string.calibration_input_start,
        R.string.calibration_input_select, R.string.calibration_input_point)[index])

    @Suppress("unused") private fun updateScene(focused: Boolean): Int {
        if (isDestroyed) return -1
        val now = SystemClock.elapsedRealtime()
        if (focused) { focusedOnce = true; unfocused = 0L }
        else if (focusedOnce) {
            if (unfocused == 0L) unfocused = now
            if (now - unfocused > 20000 && !calibration.done) { calibration.fail(getString(R.string.calibration_paused_by_system_ui_for_too_long)); return -1 }
        }
        else if (now - opened > 30000) { calibration.fail(getString(R.string.calibration_couldn_t_gain_focus)); return -1 }
        calibration.hold(!focused, System.nanoTime())
        if (armed && !calibration.started() && now - opened > 30000) {
            calibration.fail(getString(R.string.tracking_didn_t_become_ready_check_the_headset))
            TrackingService.calibration = null
        }
        val text = calibration.prompt()
        if (calibration.cancelled() && TrackingService.calibration === calibration) TrackingService.calibration = null
        calibration.assessment().let { if (it != lastAssessment) { Log.i("QFTCalibration", it); lastAssessment = it } }
        if (calibration.done && TrackingService.calibration == null) {
            if (completed == 0L) completed = now
            if (now - completed > 3500) return -1
        }
        val t = calibration.elapsed(); val pupil = calibration.isPupils
        val measuring = pupil && calibration.started() && !calibration.done
        scene[0] = if (measuring) Calibration.stimulus(t).toFloat() else 0f
        scene[1] = if (pupil || calibration.done) -1f else min(11, (t / 6).toInt()).toFloat()
        scene[2] = if (reduceMotion) -1f else (t % 6).toFloat()
        scene[3] = if (pupil) 1f else 0f
        scene[4] = distance
        scene[5] = if (measuring) 2f else if (!pupil && armed && calibration.started() && !calibration.done) 1f else 0f
        scene[6] = if (reduceMotion) 1f else 0f
        prompt = text; elapsed = t; done = calibration.done; failed = calibration.cancelled(); started = calibration.started()
        retrying = calibration.retrying(); stimulus = scene[0]
        return 1
    }
    @Suppress("unused") private fun panelSurface(surface: Surface, width: Int, height: Int) = runOnUiThread {
        if (isDestroyed) return@runOnUiThread
        try {
        val virtual = getSystemService(DisplayManager::class.java).createVirtualDisplay("qft-calibration-panel", width, height, 400, surface, 0)
        display = virtual
        presentation = Presentation(this, virtual.display).apply {
            window?.setBackgroundDrawable(ColorDrawable(0))
            ocui = Ocui(context)
            ocui.root.background = null
            setContentView(ocui.root)
            ocui.root.addView(ComposeView(context).apply { setContent { Panel() } }, ViewGroup.LayoutParams(-1, -1))
            window?.decorView?.let { it.setViewTreeLifecycleOwner(this@CalibrationActivity); it.setViewTreeSavedStateRegistryOwner(this@CalibrationActivity) }
            show()
        }
        } catch (e: Exception) {
            Log.e("QFTCalibration", "Calibration UI unavailable", e)
            closePanel()
            calibration.fail(getString(R.string.calibration_ui_couldn_t_open_previous_calibration_retained))
            stopScene()
        }
    }
    @Suppress("unused") private fun pointer(u: Float, v: Float, down: Boolean) {
        val target = if (u < 0) null else Offset(u, v)
        if (target == cursor && down == pointerDown) return
        cursor = target
        runOnUiThread {
            val view = presentation?.window?.decorView ?: return@runOnUiThread
            val now = SystemClock.uptimeMillis()
            fun send(action: Int, at: Offset) {
                MotionEvent.obtain(downTime, now, action, at.x * view.width, at.y * view.height, 0).apply { source = InputDevice.SOURCE_TOUCHSCREEN; view.dispatchTouchEvent(this); recycle() }
            }
            when {
                target != null && down && !pointerDown -> { downTime = now; send(MotionEvent.ACTION_DOWN, target) }
                target != null && down -> send(MotionEvent.ACTION_MOVE, target)
                pointerDown -> send(if (target == null) MotionEvent.ACTION_CANCEL else MotionEvent.ACTION_UP, target ?: Offset(0f, 0f))
            }
            pointerDown = down && target != null
        }
    }
    @Suppress("unused") private fun avatarReady(ok: Boolean) { avatar = if (ok) 1 else 2 }
    @Suppress("unused") private fun cancelCalibration() {
        if (::calibration.isInitialized && !calibration.done && TrackingService.calibration === calibration) calibration.fail(getString(R.string.calibration_cancelled_previous_calibration_retained))
    }
    @Suppress("unused") private fun scenePresented() { visible = true }
    @Suppress("unused") private fun startCalibration() {
        if (visible && !armed && (calibration.isPupils || avatar != 0)) { armed = true; opened = SystemClock.elapsedRealtime(); calibration.presented() }
    }

    @Composable private fun Panel() {
        val pupil = calibration.isPupils
        val measuring = pupil && started && !done
        Box(Modifier.fillMaxSize()) {
            if (measuring) PupilTimer()
            else ocui.Surface(Modifier.fillMaxSize(), "ocdialog_background") { Column(Modifier.fillMaxSize().padding(40.dp, 32.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp)) {
                when {
                    done -> {
                        ocui.Text(if (failed) getString(R.string.calibration_didn_t_finish) else getString(R.string.calibration_complete), kind = "title")
                        ocui.Text(prompt)
                        Spacer(Modifier.weight(1f)); ocui.Text(getString(R.string.returning_to_qft), kind = "title")
                    }
                    !armed || !started -> {
                        ocui.Text(if (pupil) getString(R.string.pupil_calibration) else getString(R.string.face_calibration), kind = "title")
                        ocui.Text(when {
                            armed -> prompt
                            pupil -> getString(R.string.sit_still_and_keep_light_from_leaking_into)
                            avatar == 0 -> getString(R.string.loading_the_avatar)
                            avatar == 2 -> getString(R.string.the_avatar_couldn_t_load_you_can_still)
                            else -> getString(R.string.sit_comfortably_with_the_headset_fitted_then_copy)
                        })
                        Spacer(Modifier.weight(1f))
                        Row(horizontalArrangement = Arrangement.spacedBy(16.dp), verticalAlignment = Alignment.CenterVertically) {
                            if (!armed) ocui.Button(style = "PRIMARY", label = getString(R.string.start), onClick = ::startCalibration, enabled = pupil || avatar != 0)
                            ocui.Button(label = getString(R.string.leave), onClick = ::leave)
                            if (!armed) ocui.Text(getString(R.string.point_and_pull_the_trigger_or_press_a), Modifier.weight(1f), kind = "detail")
                        }
                    }
                    else -> {
                        val step = min(11, (elapsed / 6).toInt()); val phase = elapsed % 6
                        ocui.Text(getString(Calibration.PROMPTS[step]), kind = "title", size = 40f)
                        ocui.Text(if (phase < 2) getString(R.string.get_ready) else if (phase < 5) getString(R.string.hold) else getString(R.string.relax), kind = "strong", size = 28f)
                        ocui.Progress((if (phase < 2) phase / 2 else if (phase < 5) (phase - 2) / 3 else 1.0).toFloat())
                        Spacer(Modifier.weight(1f))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            ocui.Text(getString(if (retrying) R.string.calibration_step_retry else R.string.calibration_step, step + 1), Modifier.weight(1f), kind = "detail", size = 22f)
                            ocui.Button(label = getString(R.string.leave), onClick = ::leave)
                        }
                    }
                }
            }
            }
            cursor?.let { c -> Canvas(Modifier.fillMaxSize()) { drawCircle(Color.White.copy(alpha = .9f), 10.dp.toPx(), Offset(c.x * size.width, c.y * size.height)); drawCircle(Color.Black.copy(alpha = .4f), 10.dp.toPx(), Offset(c.x * size.width, c.y * size.height), style = Stroke(2.dp.toPx())) } }
        }
    }
    @Composable private fun PupilTimer() {
        val t = elapsed
        val (phase, total, remaining) = if (t < 30) Triple(getString(R.string.adjusting_to_darkness), 30.0, 30 - t)
            else Triple(getString(if (retrying) R.string.brightness_round_retry else R.string.brightness_round, if (t < 55) 1 else 2), 25.0, 25 - (t - 30) % 25)
        val ink = Color.White.copy(alpha = if (stimulus > .5f) .22f else .30f).let { if (stimulus > .5f) Color.Black.copy(alpha = .25f) else it }
        Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
            Box(Modifier.size(150.dp), contentAlignment = Alignment.Center) {
                Canvas(Modifier.fillMaxSize()) {
                    drawArc(ink, -90f, (360 * (1 - remaining / total)).toFloat(), false, style = Stroke(3.dp.toPx(), cap = StrokeCap.Round))
                    drawCircle(ink, 5.dp.toPx())
                }
            }
            ocui.Text(getString(R.string.seconds_remaining, ceil(remaining).toInt()), color = ink.toArgb())
            ocui.Text(phase, color = ink.toArgb(), kind = "detail")
            ocui.Text(getString(R.string.b_or_y_goes_back), color = ink.toArgb(), kind = "detail")
        }
    }

    private fun leave() { cancelCalibration(); stopScene() }
    private fun closePanel() { if (::ocui.isInitialized) ocui.dismissOverlays(); presentation?.dismiss(); presentation = null; display?.release(); display = null }
    private fun returnToPanel(message: String) {
        calibrationResult = message; TrackingService.lastCalibration = message
        val panelIntent = Intent(this, MainActivity::class.java).setAction(Intent.ACTION_MAIN).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK).putExtra("page", 2)
        val task = intent.getIntExtra("returnTask", -1)
        if (task >= 0 && runCatching { getSystemService(android.app.ActivityManager::class.java).moveTaskToFront(task, 0) }.isSuccess) {
            val manager = getSystemService(android.app.NotificationManager::class.java)
            manager.createNotificationChannel(android.app.NotificationChannel("calibration", getString(R.string.calibration_results), android.app.NotificationManager.IMPORTANCE_DEFAULT))
            manager.notify(2, android.app.Notification.Builder(this, "calibration").setContentTitle(getString(R.string.qft_calibration)).setContentText(message)
                .setSmallIcon(android.R.drawable.ic_menu_view).setAutoCancel(true)
                .setContentIntent(PendingIntent.getActivity(this, 4, panelIntent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)).build())
            finishAndRemoveTask()
            return
        }
        val pending = PendingIntent.getActivity(this, 4, panelIntent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        startActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK).putExtra("extra_launch_in_home_pending_intent", pending))
        finishAndRemoveTask()
    }
    override fun onPause() { resumed = false; super.onPause() }
    override fun onDestroy() {
        if (renderer != null) { cancelCalibration(); stopScene() }
        closePanel()
        super.onDestroy()
    }
    @Deprecated("System back leaves calibration") override fun onBackPressed() { if (renderer != null) leave() else returnToPanel(getString(R.string.calibration_cancelled)) }
}
