package com.qftplus.headset

import android.content.Context
import android.content.res.Resources
import android.graphics.drawable.Drawable
import android.net.Uri
import android.view.ViewGroup
import java.lang.reflect.Constructor
import java.lang.reflect.Field
import java.lang.reflect.Member as ReflectiveMember
import java.lang.reflect.Method
import java.lang.reflect.Modifier
import java.lang.reflect.ParameterizedType
import java.lang.reflect.Type
import java.lang.reflect.WildcardType
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.ZipFile

internal class OcuiApi(private val host: Context, private val loader: ClassLoader, private val paths: List<String>) {
    private val cache = host.getSharedPreferences("ocui-api", 0)
    private val navigationSelections = mutableMapOf<Class<*>, Method>()
    private data class Member(val owner: String, val name: String, val parameters: List<String>)

    fun methods(type: Class<*>): List<Method> = (generateSequence(type) { it.superclass }
        .takeWhile { it != Any::class.java }.flatMap { it.declaredMethods.asSequence() } + type.methods.asSequence())
        .filter { it.declaringClass != Any::class.java }
        .filter { !Modifier.isStatic(it.modifiers) && !it.isBridge && !it.isSynthetic }
        .distinctBy { it.name to it.parameterTypes.toList() }.toList()

    fun fields(type: Class<*>): List<Field> = generateSequence(type) { it.superclass }
        .flatMap { it.declaredFields.asSequence() }
        .filter { Modifier.isPublic(it.modifiers) && !Modifier.isStatic(it.modifiers) && !it.isSynthetic }
        .distinctBy { it.name }.toList()

    private fun <T : ReflectiveMember> unique(role: String, candidates: List<T>): T {
        if (candidates.size == 1) return candidates[0]
        android.util.Log.e("QFT-OCUI", "$role candidates: ${candidates.joinToString()}")
        error(host.getString(R.string.ocui_api_unavailable))
    }

    fun property(obj: Any, matches: (Field) -> Boolean): Field =
        unique("${obj.javaClass.name} field", fields(obj.javaClass).filter(matches))

    fun method(obj: Any, result: Class<*>, vararg parameters: Class<*>): Method {
        val candidates = methods(obj.javaClass).filter {
            it.returnType == result && it.parameterTypes.contentEquals(parameters)
        }
        val owner = candidates.firstOrNull()?.declaringClass
        return unique("${obj.javaClass.name} method", candidates.filter { it.declaringClass == owner })
            .also { it.isAccessible = true }
    }

    fun labels(obj: Any): Method = unique("${obj.javaClass.name} labels", methods(obj.javaClass).filter {
        it.returnType == Void.TYPE && it.parameterTypes.contentEquals(arrayOf(Map::class.java)) &&
            (it.genericParameterTypes[0] as? ParameterizedType)?.actualTypeArguments?.get(1) == String::class.java
    }).also { it.isAccessible = true }

    fun dialogView(obj: Any): Method {
        val candidates = methods(obj.javaClass).filter { it.parameterCount == 0 && ViewGroup::class.java.isAssignableFrom(it.returnType) }
        val named = candidates.filter { it.name == "getDialogView" }
        return unique("${obj.javaClass.name} dialog view", named.ifEmpty { candidates }).also { it.isAccessible = true }
    }

    fun itemField(obj: Any, container: Class<*>, item: Class<*>): Field {
        val candidates = fields(obj.javaClass).filter { container.isAssignableFrom(it.type) }
        fun accepts(type: Type): Boolean = when (type) {
            is Class<*> -> type != Any::class.java && type.isAssignableFrom(item)
            is WildcardType -> (type.lowerBounds + type.upperBounds).any(::accepts)
            else -> false
        }
        val typed = candidates.filter { field ->
            (field.genericType as? ParameterizedType)?.actualTypeArguments?.firstOrNull()?.let(::accepts) == true
        }
        return unique("${obj.javaClass.name} ${container.name} items", typed.ifEmpty { candidates })
    }

    fun namedMethod(obj: Any, name: String, vararg args: Any?): Method {
        val candidates = methods(obj.javaClass).filter { method ->
            method.name == name && method.parameterCount == args.size && method.parameterTypes.zip(args).all { (type, value) ->
                    if (value == null) !type.isPrimitive else type.kotlin.javaObjectType.isInstance(value)
                }
        }
        val specific = candidates.filter { candidate ->
            candidates.none { other ->
                val pairs = candidate.parameterTypes.zip(other.parameterTypes)
                pairs.all { (base, derived) -> base.kotlin.javaObjectType.isAssignableFrom(derived.kotlin.javaObjectType) } &&
                    pairs.any { (base, derived) -> base.kotlin.javaObjectType != derived.kotlin.javaObjectType }
            }
        }
        return unique("${obj.javaClass.name}.$name", specific).also { it.isAccessible = true }
    }

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
            .map { it.owner }.distinct().mapNotNull(::resolve).toList().singleOrNull()
            ?: error(host.getString(R.string.ocui_api_unavailable))
        val spatial = method.parameterTypes[1].enumConstants!!.first { (it as Enum<*>).name == "Spatial" }
        val theme = method.invoke(null, resources, spatial) as Resources.Theme
        cache.edit().putString("theme", method.declaringClass.name).apply()
        return theme
    }

    fun navigationSelection(adapter: Any): Method = navigationSelections.getOrPut(adapter.javaClass) {
        val candidates = methods(adapter.javaClass).filter {
            it.returnType == Void.TYPE && it.parameterCount == 1 &&
                !it.parameterTypes[0].isPrimitive && it.parameterTypes[0] != Any::class.java &&
                findNavigationItem(it.parameterTypes[0]) != null
        }
        unique("${adapter.javaClass.name} navigation selection", candidates).also { it.isAccessible = true }
    }

    fun navigationItem(base: Class<*>): Constructor<*> = findNavigationItem(base)
        ?: error(host.getString(R.string.ocui_api_unavailable))

    private fun findNavigationItem(base: Class<*>): Constructor<*>? {
        fun resolve(name: String): Constructor<*>? = try {
            loader.loadClass(name).takeIf(base::isAssignableFrom)?.constructors?.singleOrNull { constructor ->
                val p = constructor.parameterTypes
                p.size == 8 && p[0] == Drawable::class.java && p[1] == Uri::class.java && p[2].isEnum &&
                    p[2].enumConstants.orEmpty().any { (it as Enum<*>).name == "SMALL" } &&
                    p.slice(3..5).all { it == String::class.java } && p[6] == Int::class.javaPrimitiveType && p[7] == Boolean::class.javaPrimitiveType
            }
        } catch (_: ReflectiveOperationException) { null } catch (_: LinkageError) { null }
        val constructor = cache.getString("navigation", null)?.let(::resolve) ?: members.asSequence()
            .filter { it.name == "<init>" }.map { it.owner }.distinct().mapNotNull(::resolve).toList().singleOrNull()
            ?: return null
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
