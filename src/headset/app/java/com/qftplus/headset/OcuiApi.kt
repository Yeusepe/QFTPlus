package com.qftplus.headset

import android.content.Context
import android.content.res.Resources
import android.graphics.drawable.Drawable
import android.net.Uri
import java.lang.reflect.Constructor
import java.lang.reflect.Method
import java.lang.reflect.Modifier
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.ZipFile

internal class OcuiApi(private val host: Context, private val loader: ClassLoader, private val paths: List<String>) {
    private val cache = host.getSharedPreferences("ocui-api", 0)
    private data class Member(val owner: String, val name: String, val parameters: List<String>)

    fun theme(resources: Resources): Resources.Theme {
        fun resolve(name: String): Method? = try {
            loader.loadClass(name).declaredMethods.singleOrNull {
                Modifier.isStatic(it.modifiers) && it.returnType == Resources.Theme::class.java &&
                    it.parameterTypes.size == 2 && it.parameterTypes[0] == Resources::class.java &&
                    it.parameterTypes[1].isEnum && it.parameterTypes[1].enumConstants.orEmpty().any { value -> (value as Enum<*>).name == "Spatial" }
            }
        } catch (_: ReflectiveOperationException) { null } catch (_: LinkageError) { null }
        val method = cache.getString("theme", null)?.let(::resolve) ?: members.asSequence()
            .filter { it.parameters.firstOrNull() == "Landroid/content/res/Resources;" }
            .mapNotNull { resolve(it.owner) }.firstOrNull()
            ?: error(host.getString(R.string.ocui_api_unavailable))
        val spatial = method.parameterTypes[1].enumConstants!!.first { (it as Enum<*>).name == "Spatial" }
        val theme = method.invoke(null, resources, spatial) as Resources.Theme
        cache.edit().putString("theme", method.declaringClass.name).apply()
        return theme
    }

    fun navigationItem(base: Class<*>): Constructor<*> {
        fun resolve(name: String): Constructor<*>? = try {
            loader.loadClass(name).takeIf(base::isAssignableFrom)?.constructors?.singleOrNull { constructor ->
                val p = constructor.parameterTypes
                p.size == 8 && p[0] == Drawable::class.java && p[1] == Uri::class.java && p[2].isEnum &&
                    p[2].enumConstants.orEmpty().any { (it as Enum<*>).name == "SMALL" } &&
                    p.slice(3..5).all { it == String::class.java } && p[6] == Int::class.javaPrimitiveType && p[7] == Boolean::class.javaPrimitiveType
            }
        } catch (_: ReflectiveOperationException) { null } catch (_: LinkageError) { null }
        val constructor = cache.getString("navigation", null)?.let(::resolve) ?: members.asSequence()
            .filter { it.name == "<init>" }.mapNotNull { resolve(it.owner) }.firstOrNull()
            ?: error(host.getString(R.string.ocui_api_unavailable))
        cache.edit().putString("navigation", constructor.declaringClass.name).apply()
        return constructor
    }

    // Read signatures without loading thousands of unrelated Settings classes or running their initializers.
    private val members by lazy {
        val found = mutableListOf<Member>()
        for (path in paths) ZipFile(path).use { apk ->
            for (entry in apk.entries()) if (entry.name.matches(Regex("classes[0-9]*\\.dex"))) {
                val bytes = apk.getInputStream(entry).use { it.readBytes() }
                val data = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                val strings = data.getInt(60); val types = data.getInt(68); val protos = data.getInt(76)
                val count = data.getInt(88); val methods = data.getInt(92)
                val names = mutableMapOf<Int, String>()
                fun string(index: Int): String = names.getOrPut(index) {
                    var at = data.getInt(strings + index * 4)
                    while ((bytes[at++].toInt() and 128) != 0) { }
                    val start = at
                    while (bytes[at].toInt() != 0) at++
                    String(bytes, start, at - start, Charsets.UTF_8)
                }
                fun type(index: Int) = string(data.getInt(types + index * 4))
                for (i in 0 until count) {
                    val at = methods + i * 8
                    val proto = protos + (data.getShort(at + 2).toInt() and 65535) * 12
                    val list = data.getInt(proto + 8)
                    if (list == 0) continue
                    val size = data.getInt(list)
                    if (size != 2 && size != 8) continue
                    val first = type(data.getShort(list + 4).toInt() and 65535)
                    if (size == 2 && first != "Landroid/content/res/Resources;") continue
                    if (size == 8 && first != "Landroid/graphics/drawable/Drawable;") continue
                    val name = string(data.getInt(at + 4))
                    if (size == 2 && type(data.getInt(proto + 4)) != "Landroid/content/res/Resources\$Theme;") continue
                    if (size == 8 && name != "<init>") continue
                    val owner = type(data.getShort(at).toInt() and 65535).removePrefix("L").removeSuffix(";").replace('/', '.')
                    found.add(Member(owner, name, (0 until size).map { type(data.getShort(list + 4 + it * 2).toInt() and 65535) }))
                }
            }
        }
        found.distinct()
    }
}
