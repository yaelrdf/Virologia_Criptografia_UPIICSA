package mx.ipn.upiicsa.prisma.ui.screens

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.ImagePreview
import mx.ipn.upiicsa.prisma.ui.components.LoadingButton
import mx.ipn.upiicsa.prisma.ui.components.PasswordField
import mx.ipn.upiicsa.prisma.ui.theme.successColor

@Composable
fun LoginScreen(vm: AppViewModel) {
    val s = vm.login
    var showServer by rememberSaveable { mutableStateOf(false) }

    Box(Modifier.fillMaxSize().imePadding(), contentAlignment = Alignment.TopCenter) {
        Column(
            Modifier.widthIn(max = 460.dp).fillMaxWidth().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("PRISMA-L", style = MaterialTheme.typography.displaySmall, fontWeight = FontWeight.Bold,
                        color = MaterialTheme.colorScheme.primary)
                    Text("Esteganografía con clave · César · Comparador",
                        style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                IconButton(onClick = { showServer = true }) {
                    Icon(Icons.Filled.Settings, contentDescription = "Configurar servidor")
                }
            }
            Text("Servidor: ${vm.serverUrl}", style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (isCleartext(vm.serverUrl)) CleartextWarning()

            ElevatedCard(Modifier.fillMaxWidth()) {
                if (s.awaitingKey) KeyStep(vm) else CredentialsStep(vm)
            }
        }
    }

    if (showServer) ServerDialog(vm, onDismiss = { showServer = false })
}

@Composable
private fun CredentialsStep(vm: AppViewModel) {
    val s = vm.login
    Column {
        PrimaryTabRow(selectedTabIndex = if (s.registering) 1 else 0) {
            Tab(selected = !s.registering, onClick = { s.registering = false; s.error = null },
                text = { Text("Iniciar sesión") })
            Tab(selected = s.registering, onClick = { s.registering = true; s.error = null },
                text = { Text("Crear cuenta") })
        }
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedTextField(
                value = s.username, onValueChange = { s.username = it },
                label = { Text("Usuario") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii, imeAction = ImeAction.Next),
                supportingText = if (s.registering) {
                    { Text("3 a 32 caracteres: letras, números, punto, guion o guion bajo") }
                } else null,
            )
            PasswordField(
                value = s.password, onValueChange = { s.password = it }, label = "Contraseña",
                supportingText = if (s.registering) "Mínimo 8 caracteres" else null,
                imeAction = if (s.registering) ImeAction.Next else ImeAction.Done,
                onDone = { vm.submitCredentials() },
            )
            if (s.registering) {
                PasswordField(
                    value = s.confirm, onValueChange = { s.confirm = it }, label = "Confirmar contraseña",
                    imeAction = ImeAction.Done, onDone = { vm.submitCredentials() },
                )
            }
            s.error?.let { ErrorCard(it) }
            LoadingButton(
                text = if (s.registering) "Crear cuenta" else "Entrar",
                loading = s.loading,
                onClick = { vm.submitCredentials() },
                modifier = Modifier.fillMaxWidth(),
                icon = if (s.registering) Icons.Filled.Add else Icons.Filled.Lock,
            )
        }
    }
}

/** Paso 2 del login: la cuenta tiene 2FA y se debe subir la imagen-llave. */
@Composable
private fun KeyStep(vm: AppViewModel) {
    val s = vm.login
    // OpenDocument permite elegir el PNG desde Descargas o cualquier proveedor de archivos
    // (el selector de fotos podría entregar una copia recomprimida).
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) vm.pickLoginKey(uri)
    }
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Paso 2 de 2", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
        Text("Selecciona tu imagen-llave", style = MaterialTheme.typography.titleLarge)
        Text(
            "La cuenta «${s.username.trim()}» tiene doble factor. Elige el archivo PNG que guardaste al activarlo.",
            style = MaterialTheme.typography.bodyMedium,
        )
        OutlinedButton(onClick = { picker.launch(arrayOf("image/*")) }, modifier = Modifier.fillMaxWidth()) {
            Text(if (s.keyUri == null) "Elegir imagen-llave" else "Cambiar imagen")
        }
        if (s.keyUri != null) {
            Text(s.keyName, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            ImagePreview(s.keyPreview, "Imagen-llave", maxHeight = 200)
        }
        s.error?.let { ErrorCard(it) }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            TextButton(onClick = { vm.cancelKeyStep() }, enabled = !s.loading) { Text("Atrás") }
            Spacer(Modifier.weight(1f))
            LoadingButton("Verificar y entrar", s.loading, onClick = { vm.submitKeyImage() }, enabled = s.keyUri != null)
        }
    }
}

@Composable
fun ServerDialog(vm: AppViewModel, onDismiss: () -> Unit) {
    var url by rememberSaveable { mutableStateOf(vm.serverUrl) }
    var invalid by rememberSaveable { mutableStateOf(false) }
    val t = vm.serverTest
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Filled.Settings, null) },
        title = { Text("Servidor") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                OutlinedTextField(
                    value = url,
                    onValueChange = { url = it; invalid = false; t.ok = null; t.message = null },
                    label = { Text("URL del servidor") },
                    singleLine = true,
                    isError = invalid,
                    supportingText = { Text(if (invalid) "URL no válida (usa http:// o https://)" else "Emulador: http://10.0.2.2:5080") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedButton(onClick = { vm.testConnection(url) }, enabled = !t.testing, modifier = Modifier.fillMaxWidth()) {
                    if (t.testing) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.size(8.dp))
                    }
                    Text("Probar conexión")
                }
                if (isCleartext(url)) CleartextWarning()
                t.message?.let {
                    Text(
                        it, style = MaterialTheme.typography.bodySmall,
                        color = if (t.ok == true) successColor else MaterialTheme.colorScheme.error,
                    )
                }
                TextButton(onClick = { vm.resetServer(); url = vm.serverUrl; invalid = false }) {
                    Text("Restablecer valor por defecto")
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { if (vm.saveServer(url)) onDismiss() else invalid = true }) { Text("Guardar") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancelar") } },
    )
}

/** true si la URL usa HTTP sin cifrar. */
private fun isCleartext(url: String) = url.trim().startsWith("http://", ignoreCase = true)

@Composable
private fun CleartextWarning() {
    Text(
        "Conexión sin cifrar (HTTP): úsala solo en una red de confianza. En la nube usa https://.",
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.tertiary,
    )
}
