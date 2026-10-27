package mx.ipn.upiicsa.signaltrace.ui

import android.content.Intent
import android.provider.Settings as AndroidSettings
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import mx.ipn.upiicsa.signaltrace.crypto.Protocol
import mx.ipn.upiicsa.signaltrace.data.Settings
import mx.ipn.upiicsa.signaltrace.telemetry.EngineState
import mx.ipn.upiicsa.signaltrace.telemetry.SensorBankCollector

@Composable
fun Section(title: String, content: @Composable () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            content()
        }
    }
}

@Composable
fun Mono(text: String) = Text(text, fontFamily = FontFamily.Monospace, fontSize = 12.sp, lineHeight = 16.sp)

@Composable
fun Kv(k: String, v: String) = Row(Modifier.fillMaxWidth()) {
    Text(k, Modifier.weight(0.42f), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
    Text(v, Modifier.weight(0.58f), style = MaterialTheme.typography.bodyMedium)
}

@Composable
fun StatusDot(level: EngineState.Level) {
    val c = when (level) {
        EngineState.Level.OK -> Color(0xFF2EA043); EngineState.Level.WARN -> Color(0xFFD29922)
        EngineState.Level.BAD -> Color(0xFFE5534B); EngineState.Level.INFO -> Color(0xFF4EA1F3); EngineState.Level.OFF -> Color.Gray
    }
    Box(Modifier.size(12.dp).background(c, CircleShape))
}

private fun groupHex(h: String) = h.chunked(4).chunked(8).joinToString("\n") { it.joinToString(" ") }

// ---------------------------------------------------------------- Estado
@Composable
fun StatusScreen(state: EngineState, ready: Boolean, onStart: () -> Unit, onStop: () -> Unit, onClearQueue: () -> Unit) {
    Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Section("Telemetría") {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                StatusDot(state.level); Text(state.status, fontWeight = FontWeight.SemiBold)
            }
            if (!ready) Text("Primero fija la huella del servidor y registra el dispositivo en «Configurar».", color = MaterialTheme.colorScheme.error)
            Button(onClick = if (state.running) onStop else onStart, enabled = ready || state.running, modifier = Modifier.fillMaxWidth(),
                colors = if (state.running) ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error) else ButtonDefaults.buttonColors()) {
                Text(if (state.running) "Detener" else "Iniciar (servicio en primer plano)")
            }
            Text("Con el servicio en primer plano la telemetría continúa con la pantalla bloqueada. La notificación permanente indica que está activa.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Section("Contadores") {
            Kv("Aceptados (202)", state.ok.toString()); Kv("Rechazados", state.rejected.toString())
            Kv("Fallos de red", state.networkErrors.toString()); Kv("En cola local", state.queued.toString())
            Kv("Sesión", state.sessionId ?: "sin handshake"); Kv("Suite", state.suite ?: "—"); Kv("Último seq", state.seq.toString())
            OutlinedButton(onClick = onClearQueue) { Text("Vaciar cola") }
        }
        Section("Sensores") {
            if (state.sources.isEmpty()) Text("Se muestran al iniciar.", color = MaterialTheme.colorScheme.onSurfaceVariant)
            state.sources.forEach { (k, v) -> Kv(if (k == "bank") "Sensores físicos" else Settings.label(k), v) }
        }
        Section("Última lectura (texto claro)") { Mono(state.lastPayload ?: "—") }
        Section("En el cable (lo que ve Eve)") { Mono(state.lastWire ?: "—") }
        Section("Bitácora") { state.log.forEach { Mono(it) } }
    }
}

// ---------------------------------------------------------------- Configurar
@Composable
fun SetupScreen(vm: AppViewModel, setup: SetupState, onScanQr: (String) -> Unit) {
    var url by remember(setup.serverUrl) { mutableStateOf(setup.serverUrl) }
    var name by remember(setup.deviceName) { mutableStateOf(setup.deviceName) }
    var user by remember { mutableStateOf("") }
    var pass by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    var confirmed by remember { mutableStateOf(false) }

    Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        setup.message?.let {
            Card { Text(it, Modifier.padding(12.dp), color = if (setup.error) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary) }
        }
        Section("Vincular con QR (recomendado)") {
            Text("En el dashboard web pulsa «Vincular teléfono (QR)». El QR es un enlace normal con la URL del servidor, la huella de su llave, la huella de su certificado TLS y un código de un solo uso.",
                style = MaterialTheme.typography.bodySmall)
            OutlinedTextField(name, { name = it }, label = { Text("Nombre del dispositivo") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            Button(onClick = { onScanQr(name) }, enabled = !setup.busy, modifier = Modifier.fillMaxWidth()) { Text("Escanear QR") }
        }
        Section("1 · Servidor") {
            OutlinedTextField(url, { url = it }, label = { Text("URL del servidor") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
            if (url.startsWith("http://")) Text("HTTP sin TLS: los paquetes van cifrados de extremo a extremo, pero el token del dispositivo viaja en claro. Usa HTTPS: el QR de vinculación ya apunta al puerto HTTPS del servidor y fija su certificado.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.tertiary)
            Button(onClick = { vm.setServerUrl(url); vm.fetchKey() }, enabled = !setup.busy) { Text("Obtener llave del servidor") }
            setup.fetchedKey?.let { key ->
                Text("Huella SHA-256 (compárala con la que imprime el servidor):", style = MaterialTheme.typography.bodySmall)
                Mono(groupHex(key.fingerprint))
                if (setup.pinnedFingerprint != key.fingerprint) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(confirmed, { confirmed = it }); Text("La huella coincide")
                    }
                    Button(onClick = vm::pin, enabled = confirmed) { Text(if (setup.pinnedFingerprint == null) "Fijar huella" else "Volver a fijar (llave nueva)") }
                }
            }
            Kv("Huella fijada", setup.pinnedFingerprint?.take(16)?.plus("…") ?: "ninguna")
        }
        Section("2 · Dispositivo") {
            if (setup.deviceId != null) {
                Kv("deviceId", setup.deviceId); Kv("Nombre", setup.deviceName)
                OutlinedButton(onClick = vm::forget) { Text("Olvidar dispositivo") }
            } else {
                OutlinedTextField(name, { name = it }, label = { Text("Nombre del dispositivo") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Text("Con usuario y contraseña:", style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(user, { user = it }, label = { Text("Usuario") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(pass, { pass = it }, label = { Text("Contraseña") }, singleLine = true, visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), modifier = Modifier.fillMaxWidth())
                Button(onClick = { vm.register(user, pass, name); pass = "" }, enabled = !setup.busy && user.isNotBlank() && pass.isNotBlank()) { Text("Registrar") }
                HorizontalDivider()
                Text("O con el código de vinculación del dashboard:", style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(code, { code = it.uppercase() }, label = { Text("Código (8 caracteres)") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Button(onClick = { vm.redeem(code, name) }, enabled = !setup.busy && code.length >= 8) { Text("Canjear código") }
            }
        }
    }
}

// ---------------------------------------------------------------- Ajustes
@Composable
fun SettingsScreen(settings: Settings, running: Boolean) {
    val ctx = LocalContext.current
    var interval by remember { mutableFloatStateOf(settings.intervalSec) }
    var suite by remember { mutableStateOf(settings.suite) }
    val available = remember { SensorBankCollector.availability(ctx) }
    val sources = remember { Settings.SOURCES.associate { it.id to mutableStateOf(settings.source(it.id)) } }
    fun setAll(on: Boolean) = Settings.SOURCES.forEach { sources[it.id]!!.value = on; settings.setSource(it.id, on) }
    Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        if (running) Card { Text("Los cambios se aplican al reiniciar la telemetría.", Modifier.padding(12.dp)) }
        Section("Intervalo de muestreo: ${"%.0f".format(interval)} s") {
            Slider(interval, { interval = it }, valueRange = 1f..30f, steps = 28, onValueChangeFinished = { settings.intervalSec = interval })
        }
        Section("Sensores expuestos al servidor") {
            Text("Por defecto se expone todo el banco de sensores. Solo los activados salen del teléfono, y siempre cifrados.",
                style = MaterialTheme.typography.bodySmall)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { setAll(true) }) { Text("Todos") }
                OutlinedButton(onClick = { setAll(false) }) { Text("Ninguno") }
            }
            Settings.SOURCES.groupBy { it.group }.forEach { (group, items) ->
                Text(group.uppercase(), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 6.dp))
                items.forEach { src ->
                    val present = available[src.id] ?: true
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(src.label)
                            if (!present) Text("No disponible en este dispositivo", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        Switch(sources[src.id]!!.value && present, { sources[src.id]!!.value = it; settings.setSource(src.id, it) }, enabled = present)
                    }
                }
            }
            Text("Si niegas un permiso (actividad física para el podómetro) solo se desactiva ese sensor. La app no usa la ubicación.",
                style = MaterialTheme.typography.bodySmall)
        }
        Section("Cifrado autenticado") {
            listOf(Protocol.SUITE_AES to "AES-256-GCM", Protocol.SUITE_CHACHA to "ChaCha20-Poly1305").forEach { (id, label) ->
                val enabled = id == Protocol.SUITE_AES || Protocol.chachaSupported()
                Row(verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(suite == id, { suite = id; settings.suite = id }, enabled = enabled)
                    Text(label + if (!enabled) " (no disponible en este Android)" else "")
                }
            }
        }
        Section("Batería") {
            Text("Algunos fabricantes detienen servicios en segundo plano. Excluye SignalTrace de la optimización de batería.", style = MaterialTheme.typography.bodySmall)
            OutlinedButton(onClick = { ctx.startActivity(Intent(AndroidSettings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) }) { Text("Abrir ajustes de batería") }
        }
        Spacer(Modifier.height(24.dp))
    }
}
