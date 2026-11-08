// Activity única de la app ABCO: aplica el tema elegido, ajusta las barras del sistema y monta la UI Compose.
package mx.ipn.upiicsa.prisma

import android.graphics.Color
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.DisposableEffect
import mx.ipn.upiicsa.prisma.data.ThemeMode
import mx.ipn.upiicsa.prisma.ui.PrismaApp
import mx.ipn.upiicsa.prisma.ui.theme.PrismaTheme

/**
 * Punto de entrada de la app. Toda la interfaz es Compose ([PrismaApp], la raíz de ABCO) y el estado vive
 * en [AppViewModel], que sobrevive a los cambios de configuración (rotación, cambio de tamaño de ventana).
 */
class MainActivity : ComponentActivity() {
    /** ViewModel compartido por todas las pantallas; `viewModels()` lo conserva entre recreaciones. */
    private val vm: AppViewModel by viewModels()

    /**
     * Activa el dibujo de borde a borde y monta la UI. El tema oscuro se decide con [AppViewModel.themeMode]
     * (Automático/Claro/Oscuro) y no solo con el ajuste del sistema.
     */
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            // SYSTEM sigue isSystemInDarkTheme(), que se recompone solo cuando el sistema cambia.
            val systemDark = isSystemInDarkTheme()
            val dark = when (vm.themeMode) {
                ThemeMode.SYSTEM -> systemDark
                ThemeMode.LIGHT -> false
                ThemeMode.DARK -> true
            }
            // Barras del sistema transparentes con íconos claros u oscuros según el tema de la app
            // (no el del sistema), para que se lean también con "Claro"/"Oscuro" forzado.
            // La clave `dark` hace que el efecto se repita cada vez que cambia el tema.
            DisposableEffect(dark) {
                val style = if (dark) SystemBarStyle.dark(Color.TRANSPARENT)
                else SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT)
                enableEdgeToEdge(statusBarStyle = style, navigationBarStyle = style)
                onDispose {}
            }
            PrismaTheme(darkTheme = dark) {
                Surface(color = MaterialTheme.colorScheme.background) {
                    PrismaApp(vm)
                }
            }
        }
    }
}
