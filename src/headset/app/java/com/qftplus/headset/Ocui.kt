package com.qftplus.headset

import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.res.AssetManager
import android.content.res.Resources
import android.graphics.drawable.Drawable
import android.net.Uri
import android.text.Editable
import android.text.TextWatcher
import android.util.AttributeSet
import android.view.ContextThemeWrapper
import android.view.View
import android.view.ViewGroup
import android.widget.CompoundButton
import android.widget.EditText
import android.widget.SeekBar
import android.widget.TextView
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.runtime.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.Modifier
import androidx.compose.foundation.background
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import java.lang.reflect.Proxy
import java.util.function.Function

internal class Ocui(host: Context) {
    companion object {
        const val PROVIDER = "com.oculus.panelapp.settings"
        const val SETTINGS_VERSION = 675101304L
    }
    val context: Context
    private val loader: ClassLoader
    val root: ViewGroup
    private val overlays = mutableListOf<() -> Unit>()

    init {
        val info = host.packageManager.getPackageInfo(PROVIDER, 0)
        require((info.applicationInfo!!.flags and (ApplicationInfo.FLAG_SYSTEM or ApplicationInfo.FLAG_UPDATED_SYSTEM_APP)) != 0) {
            "OCUI must come from the headset's system Settings app."
        }
        require(info.longVersionCode == SETTINGS_VERSION) {
            "Settings ${info.versionName} (${info.longVersionCode}) needs an updated QFT+ OCUI adapter."
        }
        val loaded = host.createPackageContext(PROVIDER, Context.CONTEXT_INCLUDE_CODE or Context.CONTEXT_IGNORE_SECURITY)
        loaded.classLoader
        val foreign = loaded.createConfigurationContext(host.resources.configuration)
        loader = foreign.classLoader
        val theme = type("X.0fv").getMethod("A00", Resources::class.java, type("X.0fw"))
            .invoke(null, foreign.resources, enum("X.0fw", "Spatial")) as Resources.Theme
        context = object : ContextThemeWrapper(host, theme) {
            override fun getResources(): Resources = foreign.resources
            override fun getAssets(): AssetManager = foreign.assets
            override fun getClassLoader(): ClassLoader = loader
        }
        root = view("popup.OCMultiLayerContainer") as ViewGroup
        root.background = android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(0xFF414141.toInt(), 0xFF272727.toInt()))
        listOf("X.18f", "X.18b", "X.0b4", "X.1Bw", "X.0zN").forEach(::type)
    }

    private fun type(name: String) = loader.loadClass(name)
    private fun enum(name: String, value: String) = type(name).enumConstants!!.first { (it as Enum<*>).name == value }
    private fun view(name: String): View = type("com.oculus.ocui.view.$name")
        .getConstructor(Context::class.java, AttributeSet::class.java).newInstance(context, null) as View
    private fun field(obj: Any, name: String): Any = obj.javaClass.getField(name).get(obj)!!
    private fun set(obj: Any, name: String, value: Any?) = obj.javaClass.getField(name).set(obj, value)
    private fun invoke(obj: Any, name: String, vararg args: Any?): Any? =
        obj.javaClass.methods.single { it.name == name && it.parameterCount == args.size }.invoke(obj, *args)
    private fun callback(name: String, action: (Array<out Any?>) -> Unit): Any =
        Proxy.newProxyInstance(loader, arrayOf(type(name))) { proxy, method, args ->
            when (method.name) {
                "equals" -> proxy === args?.firstOrNull()
                "hashCode" -> System.identityHashCode(proxy)
                "toString" -> "QFT+ OCUI callback"
                else -> { action(args ?: emptyArray()); null }
            }
        }
    private fun drawable(name: String): Drawable {
        val id = context.resources.getIdentifier(name, "drawable", PROVIDER)
        require(id != 0) { "Missing OCUI resource: $name" }
        return context.resources.getDrawable(id, context.theme)
    }
    fun icon(name: String): Drawable = if (name.startsWith("oc_icon_")) drawable(name) else Symbols.drawable(name, px(24))
    fun dismissOverlays() { overlays.toList().forEach { it() } }

    private fun field(edit: EditText) = edit.apply {
        background = android.graphics.drawable.StateListDrawable().apply {
            addState(intArrayOf(android.R.attr.state_focused), pill(0x24FFFFFF)); addState(intArrayOf(android.R.attr.state_hovered), pill(0x1CFFFFFF))
            addState(intArrayOf(), pill(0x12FFFFFF))
        }
        setTextAppearance(0x7f100058); textSize = 14f; setTextColor(android.graphics.Color.WHITE); setHintTextColor(secondaryText)
        setPaddingRelative(px(16), 0, px(16), 0)
    }
    private fun tile(fill: Int) = android.graphics.drawable.StateListDrawable().apply {
        val alpha = fill ushr 24
        addState(intArrayOf(android.R.attr.state_pressed), android.graphics.drawable.ColorDrawable(((alpha + 0x1C) shl 24) or 0xFFFFFF))
        addState(intArrayOf(android.R.attr.state_hovered), android.graphics.drawable.ColorDrawable(((alpha + 0x0E) shl 24) or 0xFFFFFF))
        addState(intArrayOf(), android.graphics.drawable.ColorDrawable(fill))
        setEnterFadeDuration(120); setExitFadeDuration(200)
    }
    private fun pill(color: Int) = android.graphics.drawable.GradientDrawable().apply { cornerRadius = px(22).toFloat(); setColor(color) }
    private fun pillButton(style: String) = android.widget.Button(context).apply {
        setTextAppearance(0x7f100058); textSize = 14.8f; isAllCaps = false; stateListAnimator = null
        minWidth = 0; minimumWidth = 0; minHeight = px(44); minimumHeight = px(44); setPaddingRelative(px(16), 0, px(16), 0)
        val (fills, ink) = when (style) {
            "PRIMARY" -> intArrayOf(0xFFFFFFFF.toInt(), 0xFFE4E6EB.toInt(), 0xFFCED0D4.toInt()) to 0xFF1C1E21.toInt()
            "DANGER" -> intArrayOf(0xFFDC143C.toInt(), 0xFFE5385A.toInt(), 0xFFEE5C78.toInt()) to android.graphics.Color.WHITE
            else -> intArrayOf(0x12FFFFFF, 0x22FFFFFF, 0x30FFFFFF) to android.graphics.Color.WHITE
        }
        background = android.graphics.drawable.StateListDrawable().apply {
            addState(intArrayOf(android.R.attr.state_pressed), pill(fills[2]))
            addState(intArrayOf(android.R.attr.state_hovered), pill(fills[1]))
            addState(intArrayOf(), pill(fills[0]))
            setEnterFadeDuration(120); setExitFadeDuration(200)
        }
        setTextColor(ink)
    }

    @Composable fun Text(text: String, modifier: Modifier = Modifier, kind: String = "body", color: Int? = null, size: Float? = null) {
        AndroidView(modifier = modifier, factory = {
            (view("text.OCTextView") as TextView).apply {
                setTextAppearance(when (kind) { "title" -> 0x7f10005b; "strong" -> 0x7f10005c; "detail" -> 0x7f100059; else -> 0x7f100058 })
                if (kind == "title") isAccessibilityHeading = true
                size?.let { textSize = it }
            }
        }, update = { if (it.text.toString() != text) it.text = text; color?.let(it::setTextColor) })
    }

    @Composable fun Surface(modifier: Modifier = Modifier, background: String = "oc_list_item_background", content: @Composable BoxScope.() -> Unit) {
        Box(modifier) {
            AndroidView(modifier = Modifier.matchParentSize(), factory = { View(context).apply {
                this.background = drawable(background); importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            } })
            content()
        }
    }

    @Composable fun Button(label: String, onClick: () -> Unit, modifier: Modifier = Modifier, style: String = "SECONDARY", enabled: Boolean = true) {
        val click by rememberUpdatedState(onClick)
        AndroidView(modifier = modifier.height(44.dp), factory = { pillButton(style).apply { setOnClickListener { click() } } }, update = {
            it.text = label; it.isEnabled = enabled; it.alpha = if (enabled) 1f else .4f
        })
    }

    @Composable fun Progress(value: Float) {
        AndroidView(modifier = Modifier.fillMaxWidth().height(12.dp), factory = { view("performance.OCProgressBar") },
            update = { invoke(it, "setProgress", (value.coerceIn(0f, 1f) * 100).toInt()) })
    }

    @Composable fun SearchField(query: String, change: (String) -> Unit, submit: () -> Unit, placeholder: String, modifier: Modifier = Modifier) {
        val changed by rememberUpdatedState(change)
        val submitted by rememberUpdatedState(submit)
        AndroidView(modifier = modifier.height(44.dp), factory = {
            android.widget.FrameLayout(context).apply {
                val edit = (view("OCTextInput") as EditText).apply {
                    setSingleLine(true); hint = placeholder; contentDescription = placeholder
                    imeOptions = android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH
                    field(this); setPaddingRelative(px(38), 0, px(40), 0)
                    addTextChangedListener(object : TextWatcher {
                        override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
                        override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) { changed(s.toString()) }
                        override fun afterTextChanged(s: Editable?) {}
                    })
                    setOnEditorActionListener { _, action, _ -> (action == android.view.inputmethod.EditorInfo.IME_ACTION_SEARCH).also { if (it) submitted() } }
                }
                fun glyph(name: String, inset: Int) = android.widget.ImageView(context).apply {
                    setImageDrawable(drawable(name)); setPadding(inset, inset, inset, inset)
                    imageTintList = android.content.res.ColorStateList.valueOf(secondaryText)
                }
                addView(edit, android.widget.FrameLayout.LayoutParams(-1, -1))
                addView(glyph("oc_icon_search_filled_24", px(15)).apply { importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO },
                    android.widget.FrameLayout.LayoutParams(px(44), -1, android.view.Gravity.START))
                addView(glyph("oc_icon_close_filled_24", px(12)).apply { contentDescription = "Clear search"; setOnClickListener { edit.setText("") } },
                    android.widget.FrameLayout.LayoutParams(px(44), -1, android.view.Gravity.END))
            }
        }, update = { frame ->
            val edit = frame.getChildAt(0) as EditText
            if (edit.text.toString() != query) { edit.setText(query); edit.setSelection(query.length) }
            frame.getChildAt(2).visibility = if (query.isEmpty()) View.GONE else View.VISIBLE
        })
    }

    @Composable fun Dropdown(options: List<String>, selected: Int, choose: (Int) -> Unit, label: String, modifier: Modifier = Modifier) {
        val changed by rememberUpdatedState(choose)
        val select = remember(options) {
            view("popup.OCSelect").apply {
                contentDescription = label
                invoke(this, "A02", options.indices.associateWith { options[it] })
                invoke(this, "A01", options.indices.toList())
                set(field(field(this, "A07"), "A0B"), "A00", callback("X.18b") { args ->
                    val index = args[0] as Int
                    invoke(this, "A00", index); changed(index)
                })
            }
        }
        DisposableEffect(select) {
            val close = { invoke(field(select, "A07"), "A02", false); Unit }
            overlays.add(close)
            onDispose { close(); overlays.remove(close) }
        }
        key(select) { AndroidView(modifier = modifier.heightIn(min = 60.dp), factory = { select }, update = { invoke(it, "A00", selected) }) }
    }

    @Composable fun Navigation(pages: List<String>, icons: List<String>, selected: Int, choose: (Int) -> Unit, modifier: Modifier = Modifier) {
        val changed by rememberUpdatedState(choose)
        val nav = remember {
            view("navigation.OCSideNav").apply {
                (field(this, "A05") as TextView).visibility = View.GONE
                (field(this, "A00") as View).apply { layoutParams = android.widget.LinearLayout.LayoutParams(-1, -1); setPaddingRelative(px(8), 0, px(8), 0) }
                (field(this, "A02") as View).visibility = View.GONE
                val constructor = type("X.0b4").getConstructor(Drawable::class.java, Uri::class.java, type("X.18Q"),
                    String::class.java, String::class.java, String::class.java, Int::class.javaPrimitiveType, Boolean::class.javaPrimitiveType)
                val items = pages.mapIndexed { i, title -> constructor.newInstance(icon(icons[i]), null, enum("X.18Q", "SMALL"), i.toString(), title, null, i, true) }
                val adapter = field(this, "A04")
                set(adapter, "A01", items)
                set(adapter, "A02", Function<Any, Any?> { item -> changed(items.indexOf(item)); null })
                val observer = adapter.javaClass.superclass.getField("A00").get(adapter)
                invoke(observer, "A00")
            }
        }
        AndroidView(modifier = modifier, factory = { nav }, update = {
            val adapter = field(it, "A04")
            invoke(adapter, "A05", (field(adapter, "A01") as List<*>)[selected])
        })
    }

    class Slot(val key: Any, val create: (Context) -> View, val height: Int = -2, val update: (View) -> Unit)

    fun toggle(checked: Boolean, label: String, change: (Boolean) -> Unit) = Slot("toggle", { view("OCToggle").apply { (this as TextView).gravity = android.view.Gravity.CENTER_VERTICAL } }) {
        val toggle = it as CompoundButton
        toggle.setOnCheckedChangeListener(null); toggle.isChecked = checked; toggle.contentDescription = label
        toggle.setOnCheckedChangeListener { _, value -> change(value) }
    }
    fun radio(selected: Boolean, label: String, choose: () -> Unit) = Slot("radio", { view("OCRadioButton") }) {
        val radio = it as CompoundButton
        radio.isChecked = selected; radio.contentDescription = label; radio.setOnClickListener { choose() }
    }
    fun button(label: String, style: String = "SECONDARY", enabled: Boolean = true, click: () -> Unit) = Slot("button:$style", {
        android.widget.FrameLayout(context).apply { addView(pillButton(style), android.widget.FrameLayout.LayoutParams(-2, px(44), android.view.Gravity.CENTER)) }
    }, height = px(44)) {
        val button = (it as ViewGroup).getChildAt(0) as TextView
        button.text = label; button.isEnabled = enabled; button.alpha = if (enabled) 1f else .4f; button.setOnClickListener { click() }
    }
    fun select(options: List<String>, selected: Int, label: String, choose: (Int) -> Unit): Slot {
        var current: (Int) -> Unit = choose
        return Slot(options, {
            view("popup.OCSelect").apply {
                contentDescription = label
                invoke(this, "A02", options.indices.associateWith { options[it] })
                invoke(this, "A01", options.indices.toList())
                set(field(field(this, "A07"), "A0B"), "A00", callback("X.18b") { args -> invoke(this, "A00", args[0] as Int); current(args[0] as Int) })
                val close = { invoke(field(this, "A07"), "A02", false); Unit }
                overlays.add(close)
                ((this as ViewGroup).getChildAt(0) as TextView).apply { layoutParams = layoutParams.apply { height = px(44) }; minHeight = px(44) }
            }
        }) { current = choose; invoke(it, "A00", selected) }
    }
    fun slider(value: Float, label: String, change: (Float) -> Unit): Slot {
        var current: (Float) -> Unit = change
        return Slot("slider", {
            (view("OCSlider") as SeekBar).apply {
                max = 1000; contentDescription = label; setPaddingRelative(px(20), paddingTop, px(20), paddingBottom)
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(bar: SeekBar, progress: Int, fromUser: Boolean) { if (fromUser) current(progress / 1000f) }
                    override fun onStartTrackingTouch(bar: SeekBar) {}
                    override fun onStopTrackingTouch(bar: SeekBar) {}
                })
            }
        }) { current = change; val bar = it as SeekBar; if (!bar.isPressed) bar.progress = (value.coerceIn(0f, 1f) * 1000).toInt() }
    }
    fun input(value: String, hint: String, label: String, widthDp: Int, keys: android.text.method.KeyListener, change: (String) -> Unit): Slot {
        var current: (String) -> Unit = change; var binding = false
        return Slot("input", {
            android.widget.FrameLayout(context).apply {
                addView((view("OCTextInput") as EditText).apply {
                    setSingleLine(true); this.hint = hint; contentDescription = label; keyListener = keys; field(this)
                    addTextChangedListener(object : TextWatcher {
                        override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
                        override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) { if (!binding) current(s.toString()) }
                        override fun afterTextChanged(s: Editable?) {}
                    })
                }, android.widget.FrameLayout.LayoutParams(px(widthDp), px(44), android.view.Gravity.CENTER))
            }
        }, height = px(44)) { current = change; val edit = (it as ViewGroup).getChildAt(0) as EditText; if (edit.text.toString() != value) { binding = true; edit.setText(value); binding = false } }
    }
    fun chevron() = Slot("chevron", { android.widget.ImageView(context).apply {
        setImageDrawable(drawable("oc_icon_chevron_right_filled_24")); imageTintList = android.content.res.ColorStateList.valueOf(attribute(0x7f040279).data)
        importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
    } }) {}

    @Composable fun Row(title: String, subtitle: String? = null, icon: String? = null, trailing: Slot? = null, below: Slot? = null,
                        modifier: Modifier = Modifier, highlighted: Boolean = false, onClick: (() -> Unit)? = null) {
        val click by rememberUpdatedState(onClick)
        key(trailing?.key, below?.key, icon) {
            AndroidView(modifier = modifier.fillMaxWidth(), factory = { ctx ->
                android.widget.FrameLayout(ctx).apply { minimumHeight = px(72); addView((view("OCListItem") as ViewGroup).apply {
                    fun slot(child: View, name: String, width: Int) = addView(child.apply { id = View.generateViewId(); tag = name }, ViewGroup.LayoutParams(width, -2))
                    (field(this, "A08") as View).visibility = View.GONE
                    setPaddingRelative(px(4), paddingTop, paddingEnd, paddingBottom)
                    background = null; foreground = null; isClickable = false; isFocusable = false
                    icon?.let { name -> slot(android.widget.ImageView(context).apply {
                        setImageDrawable(icon(name)); setPadding(px(12), px(12), px(12), px(12))
                        imageTintList = android.content.res.ColorStateList.valueOf(attribute(0x7f040279).data)
                        importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
                    }, "list_item_image_view", -2) }
                    trailing?.let { addView(it.create(ctx).apply { id = View.generateViewId(); tag = "list_item_button_layout" }, ViewGroup.LayoutParams(-2, it.height)) }
                    below?.let { slot(it.create(ctx), "list_item_bottom_component", -1) }
                }, android.widget.FrameLayout.LayoutParams(-1, -2, android.view.Gravity.CENTER_VERTICAL)) }
            }, update = { frame ->
                val item = frame.getChildAt(0) as ViewGroup
                if (frame.tag != highlighted) { frame.tag = highlighted; frame.background = tile(if (highlighted) highlight else surface) }
                (field(item, "A02") as TextView).text = title
                (field(item, "A01") as TextView).apply { text = subtitle.orEmpty(); visibility = if (subtitle.isNullOrEmpty()) View.GONE else View.VISIBLE }
                for (i in 0 until item.childCount) {
                    val child = item.getChildAt(i)
                    when (child.tag) { "list_item_button_layout" -> trailing?.update?.invoke(child); "list_item_bottom_component" -> below?.update?.invoke(child) }
                }
                if (click != null) frame.setOnClickListener { click?.invoke() } else { frame.setOnClickListener(null); frame.isClickable = false }
            })
        }
    }

    private fun attribute(id: Int) = android.util.TypedValue().also { require(context.theme.resolveAttribute(id, it, true)) { "Missing OCUI attribute ${Integer.toHexString(id)}" } }
    private fun px(dp: Int) = (dp * context.resources.displayMetrics.density).toInt()
    private val surface = 0x0EFFFFFF
    private val secondaryText by lazy { attribute(0x7f0402d6).data }
    private val highlight = 0x2AFFFFFF
    val sidebar = 0x0CFFFFFF
    private val cornerRadius = 16f

    @Composable fun Section(header: String? = null, footer: String? = null, rows: @Composable ColumnScope.() -> Unit) {
        Column(Modifier.fillMaxWidth()) {
            header?.let { Text(it, Modifier.padding(bottom = 10.dp), "strong") }
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(cornerRadius.dp)), verticalArrangement = Arrangement.spacedBy(2.dp), content = rows)
            footer?.let { Text(it, Modifier.padding(start = 20.dp, top = 8.dp, end = 20.dp), "detail", secondaryText) }
        }
    }

    @Composable fun Header(title: String, back: (() -> Unit)? = null) {
        val goBack by rememberUpdatedState(back)
        androidx.compose.foundation.layout.Row(Modifier.fillMaxWidth().height(44.dp), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
            if (back != null) {
                BackHandler { goBack?.invoke() }
                AndroidView(modifier = Modifier.size(44.dp), factory = { view("button.OCBackButton").apply { setOnClickListener { goBack?.invoke() } } })
                Spacer(Modifier.width(8.dp))
            }
            Text(title, kind = "title")
        }
    }

    @Composable fun Dialog(title: String, message: String, action: String, dismiss: () -> Unit, confirm: () -> Unit) {
        val cancel by rememberUpdatedState(dismiss)
        val accept by rememberUpdatedState(confirm)
        BackHandler { cancel() }
        DisposableEffect(title, message, action) {
            val cls = type("com.oculus.ocui.view.dialog.OCDialog")
            val builder = cls.getMethod("builder", View::class.java).invoke(null, root)
            set(builder, "A09", title); set(builder, "A07", message)
            invoke(builder, "A0E", callback("X.1Bw") { accept() }, action, true)
            invoke(builder, "A0D", callback("X.1Bw") { cancel() }, "Cancel")
            set(builder, "A06", callback("X.0zN") { cancel() })
            val dialog = invoke(builder, "A0C")!!
            var closed = false
            val close = { if (!closed) { closed = true; invoke(dialog, "dismiss"); cancel() }; Unit }
            overlays.add(close)
            invoke(dialog, "show")
            onDispose { close(); overlays.remove(close) }
        }
    }
}
