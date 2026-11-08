// Preferencias locales de ABCO: URL del servidor configurable y modo de tema (Automático/Claro/Oscuro).
package mx.ipn.upiicsa.prisma.data

import android.content.Context
import androidx.core.content.edit
import mx.ipn.upiicsa.prisma.BuildConfig

/**
 * Modo de color de la app. SYSTEM sigue el ajuste del teléfono.
 *
 * @property label texto que se muestra en el selector de tema.
 */
enum class ThemeMode(val label: String) {
    SYSTEM("Automático"),
    LIGHT("Claro"),
    DARK("Oscuro"),
}

/**
 * Preferencias de la app en SharedPreferences: URL del servidor (por defecto, la de BuildConfig)
 * y modo de tema. Usa el mismo archivo que las versiones anteriores para conservar la URL guardada.
 *
 * @param context contexto con el que se abre el archivo `prisma_settings` (nombre heredado de PRISMA-L).
 */
class AppPrefs(context: Context) {
    /** Archivo privado de la app; MODE_PRIVATE impide que otras apps lo lean. */
    private val prefs = context.getSharedPreferences("prisma_settings", Context.MODE_PRIVATE)

    /**
     * URL base del servidor. Si no hay una guardada (o está vacía) devuelve la compilada en
     * `BuildConfig.DEFAULT_SERVER_URL`. Al guardar se quitan espacios y la diagonal final para que las
     * rutas `/api/...` se concatenen sin dobles diagonales.
     */
    var serverUrl: String
        get() = prefs.getString(KEY_URL, null)?.takeIf { it.isNotBlank() } ?: BuildConfig.DEFAULT_SERVER_URL
        set(value) = prefs.edit { putString(KEY_URL, value.trim().trimEnd('/')) }

    /** Borra la URL guardada para volver al valor con el que se compiló la app. */
    fun reset() = prefs.edit { remove(KEY_URL) }

    /**
     * Modo de tema guardado por nombre de la constante. Un valor desconocido (p. ej. de otra versión)
     * vuelve a [ThemeMode.SYSTEM].
     */
    var themeMode: ThemeMode
        get() = prefs.getString(KEY_THEME, null)
            ?.let { name -> ThemeMode.entries.firstOrNull { it.name == name } }
            ?: ThemeMode.SYSTEM
        set(value) = prefs.edit { putString(KEY_THEME, value.name) }

    /** Claves de SharedPreferences. */
    private companion object {
        const val KEY_URL = "server_url"
        const val KEY_THEME = "theme_mode"
    }
}
