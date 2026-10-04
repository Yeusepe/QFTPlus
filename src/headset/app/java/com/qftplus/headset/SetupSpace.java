package com.qftplus.headset;

import java.io.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.zip.*;
import org.json.*;

final class SetupSpace {
    static final String APK = "/system_ext/app/EnvironmentNuxD/EnvironmentNuxD.apk";

    private static final class Table {
        final ByteBuffer b; final int pos, vtable, vsize;
        Table(ByteBuffer b, int pos) { this.b = b; this.pos = pos; vtable = pos - b.getInt(pos); vsize = b.getShort(vtable) & 0xffff; }
        int offset(int field) { int o = 4 + 2 * field; return o < vsize ? b.getShort(vtable + o) & 0xffff : 0; }
        int at(int field) { int o = offset(field); return o == 0 ? -1 : pos + o; }
        int indirect(int field) { int p = at(field); return p < 0 ? -1 : p + b.getInt(p); }
        int u8(int field) { int p = at(field); return p < 0 ? 0 : b.get(p) & 0xff; }
        int u16(int field) { int p = at(field); return p < 0 ? 0 : b.getShort(p) & 0xffff; }
        int u32(int field) { int p = at(field); return p < 0 ? 0 : b.getInt(p); }
        long u64(int field) { int p = at(field); return p < 0 ? 0 : b.getLong(p); }
        ByteBuffer bytes(int field) { return bytes(field, 1); }
        ByteBuffer bytes(int field, int elementSize) {
            int p = indirect(field); if (p < 0) return ByteBuffer.allocate(0);
            ByteBuffer v = b.duplicate().order(ByteOrder.LITTLE_ENDIAN); v.position(p + 4).limit(p + 4 + b.getInt(p) * elementSize); return v.slice().order(ByteOrder.LITTLE_ENDIAN);
        }
        List<Table> tables(int field) {
            int p = indirect(field); List<Table> out = new ArrayList<>(); if (p < 0) return out;
            for (int i = 0, n = b.getInt(p); i < n; i++) { int e = p + 4 + 4 * i; out.add(new Table(b, e + b.getInt(e))); }
            return out;
        }
        String string(int field) { ByteBuffer v = bytes(field); byte[] s = new byte[v.remaining()]; v.get(s); return new String(s, StandardCharsets.UTF_8); }
        String ref(int field) { int p = at(field); return p < 0 ? "" : Long.toUnsignedString(b.getLong(p)) + "/" + Long.toUnsignedString(b.getLong(p + 8)) + "/" + Integer.toUnsignedString(b.getInt(p + 16)); }
        static Table root(ByteBuffer b, String id) {
            b.order(ByteOrder.LITTLE_ENDIAN);
            byte[] magic = new byte[4]; b.position(4); b.get(magic); b.position(0);
            if (!id.equals(new String(magic, StandardCharsets.US_ASCII))) throw new IllegalArgumentException("Not a " + id + " asset");
            return new Table(b, b.getInt(0));
        }
    }

    static final class Draw {
        float[] world; float[] positions, uvs; short[] indices;
        int glFormat, width, height, mips; byte[] texels; boolean blend; float radius;
    }

    private final Map<String, byte[]> files = new HashMap<>();
    private final Map<String, String> assets = new HashMap<>();

    private byte[] file(String ref) {
        byte[] data = files.get("content/" + assets.get(ref));
        if (data == null) throw new IllegalStateException("Setup space asset missing: " + ref);
        return data;
    }
    private static String ref(JSONObject j) throws JSONException {
        return j.getString("packageOrRemoteId") + "/" + j.getString("ingestionId") + "/" + Integer.toUnsignedString((int)j.getLong("targetId"));
    }

    static List<Draw> read() throws Exception {
        SetupSpace s = new SetupSpace();
        try (ZipFile apk = new ZipFile(APK); ZipInputStream scene = new ZipInputStream(apk.getInputStream(apk.getEntry("assets/scene.zip")))) {
            for (ZipEntry e; (e = scene.getNextEntry()) != null; ) if (!e.isDirectory()) s.files.put(e.getName(), readAll(scene));
        }
        ByteBuffer manifest = ByteBuffer.wrap(s.files.get("content/assets.manifest")).order(ByteOrder.LITTLE_ENDIAN);
        if (manifest.getInt(0) != 0x484d5341 || manifest.getInt(4) != 2) throw new IllegalStateException("Unsupported setup space manifest");
        manifest.position(16); ByteBuffer body = manifest.slice().order(ByteOrder.LITTLE_ENDIAN);
        for (Table entry : new Table(body, body.getInt(0)).tables(1)) s.assets.put(entry.ref(0), entry.string(1));

        JSONObject shell = new JSONObject(new String(s.files.get("content/configs/shellconfig.jsonc"), StandardCharsets.UTF_8));
        JSONObject space = new JSONObject(new String(s.file(ref(shell.getJSONObject("firstWorldAssetId"))), StandardCharsets.UTF_8));
        JSONObject template = new JSONObject(new String(s.file(ref(space.getJSONArray("entities").getJSONObject(0).getJSONObject("type"))), StandardCharsets.UTF_8));
        Map<String, JSONObject> entities = new HashMap<>(); Map<String, String> parents = new HashMap<>();
        JSONArray list = template.getJSONArray("entities");
        for (int i = 0; i < list.length(); i++) entities.put(list.getJSONObject(i).getString("id"), list.getJSONObject(i));
        JSONArray relations = template.optJSONArray("relationships");
        for (int i = 0; relations != null && i < relations.length(); i++) {
            JSONObject r = relations.getJSONObject(i);
            if ("RelationChildOf".equals(r.optString("relationshipType"))) parents.put(r.getString("source"), r.getString("destination"));
        }
        List<Draw> draws = new ArrayList<>();
        for (JSONObject entity : entities.values()) {
            JSONObject mesh = component(entity, "MeshPlatformComponent");
            if (mesh == null || !mesh.optBoolean("isVisibleSelf", true)) continue;
            JSONObject materials = component(entity, "MaterialPlatformComponent");
            JSONArray overrides = materials == null ? null : materials.optJSONArray("materials");
            Table root = Table.root(ByteBuffer.wrap(s.file(ref(mesh.getJSONObject("mesh")))), "MESH");
            float radius = Float.intBitsToFloat(root.u32(4));
            List<Table> submeshes = root.tables(1).get(0).tables(0);
            for (int k = 0; k < submeshes.size(); k++) {
                Table sub = submeshes.get(k);
                Table material = overrides != null && k < overrides.length()
                    ? Table.root(ByteBuffer.wrap(s.file(ref(overrides.getJSONObject(k)))), "MATL") : Table.root(sub.bytes(3), "MATL");
                Draw d = s.geometry(sub.tables(0).get(0), sub.bytes(1));
                s.texture(d, material);
                d.blend = material.u32(2) == 2; d.radius = radius; d.world = world(entity.getString("id"), entities, parents);
                draws.add(d);
            }
        }
        draws.sort((a, b) -> a.blend != b.blend ? (a.blend ? 1 : -1) : Float.compare(b.radius, a.radius));
        return draws;
    }

    private Draw geometry(Table stream, ByteBuffer indexBytes) {
        int count = stream.u32(1); ByteBuffer data = stream.bytes(2), attrs = stream.bytes(3, 4);
        int stride = 0, position = -1, uv = -1;
        for (int i = 0; i < attrs.remaining(); i += 4) {
            int semantic = attrs.get(i) & 0xff, format = attrs.get(i + 1) & 0xff;
            if (semantic == 0) position = stride; else if (semantic == 5) uv = stride;
            stride += new int[]{0, 1, 2, 4}[format >> 4] * ((format & 15) + 1);
        }
        if (position < 0 || uv < 0 || stride * count != data.remaining()) throw new IllegalStateException("Unsupported setup space mesh");
        Draw d = new Draw(); d.positions = new float[count * 3]; d.uvs = new float[count * 2];
        for (int v = 0; v < count; v++) {
            for (int c = 0; c < 3; c++) d.positions[v * 3 + c] = data.getFloat(v * stride + position + c * 4);
            for (int c = 0; c < 2; c++) d.uvs[v * 2 + c] = half(data.getShort(v * stride + uv + c * 2));
        }
        d.indices = new short[indexBytes.remaining() / 2];
        indexBytes.asShortBuffer().get(d.indices);
        return d;
    }
    private void texture(Draw d, Table material) {
        Table binding = material.tables(4).get(0);
        Table tex = Table.root(ByteBuffer.wrap(file(binding.ref(0))), "TXTR");
        int format = tex.u8(2);
        int[] astc = {0x93B0, 0x93B4, 0x93B7, 0x93BB, 0x93BD};
        if (format < 16 || format > 25) throw new IllegalStateException("Unsupported setup space texture format " + format);
        d.glFormat = astc[(format - 16) / 2] + 0x20;
        d.width = tex.u16(3); d.height = tex.u16(4); d.mips = tex.u8(6);
        ByteBuffer payload = tex.bytes(9); d.texels = new byte[payload.remaining()]; payload.get(d.texels);
    }
    private static JSONObject component(JSONObject entity, String name) throws JSONException {
        JSONArray components = entity.optJSONArray("components");
        for (int i = 0; components != null && i < components.length(); i++) {
            JSONObject data = components.getJSONObject(i).getJSONObject("data");
            if (data.optString("class").endsWith("::" + name)) return data.optJSONObject("data");
        }
        return null;
    }
    private static float[] world(String id, Map<String, JSONObject> entities, Map<String, String> parents) throws JSONException {
        float[] m = {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
        for (String at = id; at != null; at = parents.get(at)) {
            JSONObject t = component(entities.get(at), "TransformPlatformComponent");
            if (t == null) continue;
            JSONObject p = t.optJSONObject("localPosition"), s = t.optJSONObject("localScale"), r = t.optJSONObject("localRotation");
            if (r != null && (r.optDouble("x", 0) != 0 || r.optDouble("y", 0) != 0 || r.optDouble("z", 0) != 0))
                throw new IllegalStateException("Setup space uses rotations this reader doesn't support");
            float sx = s == null ? 1 : (float)s.optDouble("x", 1), sy = s == null ? 1 : (float)s.optDouble("y", 1), sz = s == null ? 1 : (float)s.optDouble("z", 1);
            float tx = p == null ? 0 : (float)p.optDouble("x", 0), ty = p == null ? 0 : (float)p.optDouble("y", 0), tz = p == null ? 0 : (float)p.optDouble("z", 0);
            for (int c = 0; c < 4; c++) { m[c*4] *= sx; m[c*4+1] *= sy; m[c*4+2] *= sz; m[c*4] += tx * m[c*4+3]; m[c*4+1] += ty * m[c*4+3]; m[c*4+2] += tz * m[c*4+3]; }
        }
        return m;
    }
    private static float half(short h) {
        int s = (h >> 15) & 1, e = (h >> 10) & 31, f = h & 1023;
        float v = e == 0 ? f / 1024f * (float)Math.pow(2, -14) : e == 31 ? Float.NaN : (1 + f / 1024f) * (float)Math.pow(2, e - 15);
        return s == 1 ? -v : v;
    }
    private static byte[] readAll(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream(); byte[] b = new byte[65536];
        for (int n; (n = in.read(b)) > 0; ) out.write(b, 0, n);
        return out.toByteArray();
    }

    public static void main(String[] args) throws Exception {
        List<Draw> draws = read();
        int blended = 0;
        for (Draw d : draws) {
            if (d.blend) blended++;
            if (d.indices.length % 3 != 0 || d.texels.length == 0) throw new AssertionError("Bad draw");
            System.out.printf(Locale.ROOT, "%s %dx%d mips %d verts %d tris %d radius %.1f%n", d.blend ? "blend" : "opaque", d.width, d.height, d.mips, d.positions.length / 3, d.indices.length / 3, d.radius);
        }
        if (draws.size() < 5 || blended == 0) throw new AssertionError("Setup space incomplete: " + draws.size());
        System.out.println("SetupSpace checks passed");
    }
}
