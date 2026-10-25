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

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()

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
