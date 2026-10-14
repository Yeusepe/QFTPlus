package com.qftplus.headset;

import android.content.Context;
import java.io.*;
import java.nio.file.*;
import java.util.Arrays;
import java.util.concurrent.ThreadLocalRandom;

final class AvatarAssets {
    static synchronized File prepare(Context context, boolean choose) throws IOException {
        File folder = new File(context.getFilesDir(), "avatar");
        Files.createDirectories(folder.toPath());
        copy(context, "avatar/OvrAvatar2Assets.zip", new File(folder, "OvrAvatar2Assets.zip"));
        String[] presets = context.getAssets().list("avatar/presets");
        if (presets == null || presets.length == 0) throw new IOException(context.getString(R.string.avatar_preview_unavailable));
        var settings = context.getSharedPreferences("settings", 0);
        String selected = settings.getString("avatarPreset", "");
        if (choose || !Arrays.asList(presets).contains(selected)) {
            selected = presets[ThreadLocalRandom.current().nextInt(presets.length)];
            settings.edit().putString("avatarPreset", selected).apply();
        }
        File preset = new File(folder, selected);
        copy(context, "avatar/presets/" + selected, preset);
        return preset;
    }

    private static void copy(Context context, String asset, File destination) throws IOException {
        if (destination.isFile()) return;
        File temporary = new File(destination.getPath() + ".tmp");
        try (InputStream input = context.getAssets().open(asset)) {
            Files.copy(input, temporary.toPath(), StandardCopyOption.REPLACE_EXISTING);
            Files.move(temporary.toPath(), destination.toPath(), StandardCopyOption.REPLACE_EXISTING);
        } finally { temporary.delete(); }
    }
}
