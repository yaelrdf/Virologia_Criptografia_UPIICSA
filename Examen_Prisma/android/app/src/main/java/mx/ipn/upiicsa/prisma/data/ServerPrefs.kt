package mx.ipn.upiicsa.prisma.data

import android.content.Context
import androidx.core.content.edit
import mx.ipn.upiicsa.prisma.BuildConfig

/** URL del servidor guardada en SharedPreferences (por defecto, la de BuildConfig). */
class ServerPrefs(context: Context) {
    private val prefs = context.getSharedPreferences("prisma_settings", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = prefs.getString(KEY_URL, null)?.takeIf { it.isNotBlank() } ?: BuildConfig.DEFAULT_SERVER_URL
        set(value) = prefs.edit { putString(KEY_URL, value.trim().trimEnd('/')) }

    fun reset() = prefs.edit { remove(KEY_URL) }

    private companion object {
        const val KEY_URL = "server_url"
    }
}
