package mx.ipn.upiicsa.prisma.data

import android.content.Context
import androidx.core.content.edit
import mx.ipn.upiicsa.prisma.BuildConfig

/** Modo de color de la app. SYSTEM sigue el ajuste del teléfono. */
enum class ThemeMode(val label: String) {
    SYSTEM("Automático"),
    LIGHT("Claro"),
    DARK("Oscuro"),
}

/**
 * Preferencias de la app en SharedPreferences: URL del servidor (por defecto, la de BuildConfig)
 * y modo de tema. Usa el mismo archivo que las versiones anteriores para conservar la URL guardada.
 */
class AppPrefs(context: Context) {
    private val prefs = context.getSharedPreferences("prisma_settings", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = prefs.getString(KEY_URL, null)?.takeIf { it.isNotBlank() } ?: BuildConfig.DEFAULT_SERVER_URL
        set(value) = prefs.edit { putString(KEY_URL, value.trim().trimEnd('/')) }

    fun reset() = prefs.edit { remove(KEY_URL) }

    var themeMode: ThemeMode
        get() = prefs.getString(KEY_THEME, null)
            ?.let { name -> ThemeMode.entries.firstOrNull { it.name == name } }
            ?: ThemeMode.SYSTEM
        set(value) = prefs.edit { putString(KEY_THEME, value.name) }

    private companion object {
        const val KEY_URL = "server_url"
        const val KEY_THEME = "theme_mode"
    }
}
