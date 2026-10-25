package mx.ipn.upiicsa.signaltrace

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Build
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import mx.ipn.upiicsa.signaltrace.telemetry.TelemetryEngine
import mx.ipn.upiicsa.signaltrace.telemetry.TelemetryService
import mx.ipn.upiicsa.signaltrace.ui.AppViewModel
import mx.ipn.upiicsa.signaltrace.ui.SettingsScreen
import mx.ipn.upiicsa.signaltrace.ui.SetupScreen
import mx.ipn.upiicsa.signaltrace.ui.StatusScreen

class MainActivity : ComponentActivity() {
    private val vm: AppViewModel by viewModels()
    private var pendingQrName = ""

    // Runtime permissions (PLAN §13.4): each denied permission only disables its source; the service starts anyway.
    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        TelemetryService.start(this)
    }

    private val scanner = registerForActivityResult(ScanContract()) { result ->
        result.contents?.let { vm.applyQr(it, pendingQrName) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            val scheme = if (isSystemInDarkTheme())
                darkColorScheme(primary = Color(0xFF4EA1F3), secondary = Color(0xFF1B998B), tertiary = Color(0xFFD29922))
            else lightColorScheme(primary = Color(0xFF0F6CBD), secondary = Color(0xFF1B998B), tertiary = Color(0xFF9A6700))
            MaterialTheme(colorScheme = scheme) {
                var tab by rememberSaveable { mutableIntStateOf(if (vm.settings.isRegistered && vm.settings.isPinned) 0 else 1) }
                val engine by TelemetryEngine.state.collectAsStateWithLifecycle()
                val setup by vm.setup.collectAsStateWithLifecycle()
                Scaffold(bottomBar = {
                    NavigationBar {
                        NavigationBarItem(tab == 0, { tab = 0 }, { Icon(Icons.Filled.Home, null) }, label = { Text("Estado") })
                        NavigationBarItem(tab == 1, { tab = 1 }, { Icon(Icons.Filled.Build, null) }, label = { Text("Configurar") })
                        NavigationBarItem(tab == 2, { tab = 2 }, { Icon(Icons.Filled.Settings, null) }, label = { Text("Ajustes") })
                    }
                }) { inner ->
                    val m = Modifier.padding(inner)
                    androidx.compose.foundation.layout.Box(m) {
                        when (tab) {
                            0 -> StatusScreen(engine, ready = setup.deviceId != null && setup.pinnedFingerprint != null,
                                onStart = ::startTelemetry, onStop = { TelemetryService.stop(this@MainActivity) },
                                onClearQueue = { TelemetryEngine.clearQueue(this@MainActivity) })
                            1 -> SetupScreen(vm, setup) { name ->
                                pendingQrName = name
                                scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt("Escanea el QR del dashboard").setBeepEnabled(false).setOrientationLocked(false))
                            }
                            else -> SettingsScreen(vm.settings, engine.running)
                        }
                    }
                }
            }
        }
    }

    /** Started only from the visible UI (Android 12+ forbids starting foreground services from the background). */
    private fun startTelemetry() {
        val wanted = mutableListOf(Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION, Manifest.permission.READ_PHONE_STATE)
        if (Build.VERSION.SDK_INT >= 33) wanted += Manifest.permission.POST_NOTIFICATIONS
        permissions.launch(wanted.toTypedArray())
    }
}
