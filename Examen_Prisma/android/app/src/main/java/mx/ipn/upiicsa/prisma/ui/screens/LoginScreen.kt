// Pantalla de inicio de sesión de ABCO: login y registro, paso de la imagen-llave (2FA),
// «¿Olvidaste tu contraseña?» y diálogo de ajustes (tema y URL del servidor con aviso para HTTP).
package mx.ipn.upiicsa.prisma.ui.screens

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
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
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.HorizontalDivider
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
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.R
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.ImagePreview
import mx.ipn.upiicsa.prisma.ui.components.LoadingButton
import mx.ipn.upiicsa.prisma.ui.components.PasswordField
import mx.ipn.upiicsa.prisma.ui.components.ThemeModeSelector
import mx.ipn.upiicsa.prisma.ui.components.bringIntoViewOnFocus
import mx.ipn.upiicsa.prisma.ui.theme.successColor

/**
 * Inicio de sesión. En pantallas anchas (≥ 720 dp disponibles): logo y descripción a la izquierda,
 * formulario a la derecha. En teléfono: una columna con el logo junto al nombre.
 *
 * Lee [AppViewModel.login] (a través de las tarjetas) y [AppViewModel.serverUrl]. Los diálogos de ajustes
 * y de contraseña olvidada se controlan con estado local guardable, así sobreviven a la rotación.
 */
@Composable
fun LoginScreen(vm: AppViewModel) {
    var showSettings by rememberSaveable { mutableStateOf(false) }
    var showForgot by rememberSaveable { mutableStateOf(false) }

    BoxWithConstraints(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        // 720 dp: con menos, dos columnas dejarían el formulario demasiado angosto para los campos y botones.
        val twoColumns = maxWidth >= 720.dp
        Column(
            // imePadding antes de verticalScroll: el contenido se puede desplazar por encima del teclado.
            Modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState())
                .padding(horizontal = if (twoColumns) 32.dp else 16.dp, vertical = 16.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            if (twoColumns) {
                Row(
                    Modifier.widthIn(max = 1000.dp).fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(32.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Spacer(Modifier.weight(1f))
                            SettingsButton { showSettings = true }
                        }
                        Brand(logoSize = 128.dp, large = true)
                        Description()
                        ServerInfo(vm)
                    }
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                        LoginCard(vm, onForgot = { showForgot = true })
                    }
                }
            } else {
                Column(
                    Modifier.widthIn(max = 480.dp).fillMaxWidth(),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Brand(logoSize = 64.dp, large = false, modifier = Modifier.weight(1f))
                        SettingsButton { showSettings = true }
                    }
                    ServerInfo(vm)
                    LoginCard(vm, onForgot = { showForgot = true })
                }
            }
        }
    }

    if (showSettings) SettingsDialog(vm, onDismiss = { showSettings = false })
    if (showForgot) ForgotPasswordDialog(onDismiss = { showForgot = false })
}

/** Botón de engrane que abre el diálogo de ajustes (tema y servidor). */
@Composable
private fun SettingsButton(onClick: () -> Unit) {
    IconButton(onClick = onClick) {
        Icon(Icons.Filled.Settings, contentDescription = "Ajustes (tema y servidor)")
    }
}

/**
 * Logo del panda junto al nombre ABCO.
 *
 * @param large usa el título más grande (diseño de 2 columnas).
 */
@Composable
private fun Brand(logoSize: Dp, large: Boolean, modifier: Modifier = Modifier) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Image(
            painter = painterResource(R.drawable.logo_panda),
            contentDescription = "Logo de ABCO",
            modifier = Modifier.size(logoSize),
        )
        Column {
            Text(
                "ABCO",
                style = if (large) MaterialTheme.typography.displayMedium else MaterialTheme.typography.displaySmall,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.primary,
            )
            Text(
                "Esteganografía con clave · César · Comparador",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** Lista breve de funciones de la app; solo se muestra en el diseño de 2 columnas, donde hay espacio. */
@Composable
private fun Description() {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Bullet("Oculta mensajes cifrados con tu frase dentro de imágenes PNG.")
        Bullet("Revela mensajes ocultos con la misma frase.")
        Bullet("Cifra y rompe el cifrado César, y compara los algoritmos.")
        Bullet("Protege tu cuenta con una imagen-llave como segundo factor.")
    }
}

/** Elemento de lista con viñeta. */
@Composable
private fun Bullet(text: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("•", color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.bodyLarge)
        Text(text, style = MaterialTheme.typography.bodyLarge)
    }
}

/** URL del servidor en uso y, si es HTTP, el aviso de conexión sin cifrar. */
@Composable
private fun ServerInfo(vm: AppViewModel) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text("Servidor: ${vm.serverUrl}", style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (isCleartext(vm.serverUrl)) CleartextWarning()
    }
}

/** Tarjeta del login: muestra el paso de usuario y contraseña o, si el servidor pidió 2FA, el de la imagen-llave. */
@Composable
private fun LoginCard(vm: AppViewModel, onForgot: () -> Unit) {
    ElevatedCard(Modifier.fillMaxWidth()) {
        if (vm.login.awaitingKey) KeyStep(vm) else CredentialsStep(vm, onForgot)
    }
}

/**
 * Paso 1: pestañas «Iniciar sesión» / «Crear cuenta», campos y botón principal. Al iniciar sesión también
 * ofrece «¿Olvidaste tu contraseña?».
 *
 * @param onForgot abre el diálogo de contraseña olvidada.
 */
@Composable
private fun CredentialsStep(vm: AppViewModel, onForgot: () -> Unit) {
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
                label = { Text("Usuario") }, singleLine = true,
                modifier = Modifier.fillMaxWidth().bringIntoViewOnFocus(),
                // Ascii: teclado sin acentos ni emoji, acorde con los caracteres permitidos en el usuario.
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii, imeAction = ImeAction.Next),
                supportingText = if (s.registering) {
                    { Text("3 a 32 caracteres: letras, números, punto, guion o guion bajo") }
                } else null,
            )
            PasswordField(
                value = s.password, onValueChange = { s.password = it }, label = "Contraseña",
                supportingText = if (s.registering) "Mínimo 8 caracteres" else null,
                // Al iniciar sesión «Listo» envía el formulario; al registrar, pasa a la confirmación.
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
            if (!s.registering) {
                TextButton(onClick = onForgot, modifier = Modifier.align(Alignment.CenterHorizontally)) {
                    Text("¿Olvidaste tu contraseña?")
                }
            }
        }
    }
}

/**
 * Diálogo de «¿Olvidaste tu contraseña?»: no hay recuperación automática; el administrador la restablece
 * y el usuario recibe una contraseña temporal que debe cambiar al entrar.
 */
@Composable
private fun ForgotPasswordDialog(onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Filled.Info, null) },
        title = { Text("¿Olvidaste tu contraseña?") },
        text = { Text("Contacta al administrador para que restablezca tu contraseña.") },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Entendido") } },
    )
}

/**
 * Paso 2 del login: la cuenta tiene 2FA y se debe subir la imagen-llave.
 * Lee `keyUri`, `keyName` y `keyPreview` de [AppViewModel.login]; dispara [AppViewModel.submitKeyImage]
 * o vuelve atrás con [AppViewModel.cancelKeyStep] (deshabilitado mientras se verifica).
 */
@Composable
private fun KeyStep(vm: AppViewModel) {
    val s = vm.login
    // OpenDocument permite elegir el PNG desde Descargas o cualquier proveedor de archivos
    // (el selector de fotos podría entregar una copia recomprimida o sin los metadatos originales, y la
    // imagen-llave debe llegar al servidor byte por byte).
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
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            TextButton(onClick = { vm.cancelKeyStep() }, enabled = !s.loading) { Text("Atrás") }
            Spacer(Modifier.weight(1f))
            LoadingButton("Verificar y entrar", s.loading, onClick = { vm.submitKeyImage() }, enabled = s.keyUri != null)
        }
    }
}

/**
 * Ajustes antes de iniciar sesión: tema de la app y URL del servidor.
 *
 * La URL se edita en un estado local y solo se guarda al pulsar «Guardar» (si es válida); «Probar
 * conexión» prueba la URL escrita sin guardarla. Lee [AppViewModel.serverTest] para el resultado.
 *
 * @param onDismiss cierra el diálogo.
 */
@Composable
fun SettingsDialog(vm: AppViewModel, onDismiss: () -> Unit) {
    var url by rememberSaveable { mutableStateOf(vm.serverUrl) }
    var invalid by rememberSaveable { mutableStateOf(false) }
    val t = vm.serverTest
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Filled.Settings, null) },
        title = { Text("Ajustes") },
        text = {
            Column(
                // Scroll propio: en horizontal o con el teclado abierto el diálogo puede no caber.
                Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Text("Tema", style = MaterialTheme.typography.titleSmall)
                ThemeModeSelector(vm.themeMode, vm::changeThemeMode)
                HorizontalDivider()
                Text("Servidor", style = MaterialTheme.typography.titleSmall)
                OutlinedTextField(
                    value = url,
                    // Al editar se borra el resultado de la prueba anterior, que ya no corresponde a esta URL.
                    onValueChange = { url = it; invalid = false; t.ok = null; t.message = null },
                    label = { Text("URL del servidor") },
                    singleLine = true,
                    isError = invalid,
                    supportingText = { Text(if (invalid) "URL no válida (usa http:// o https://)" else "Emulador: http://10.0.2.2:5080") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    modifier = Modifier.fillMaxWidth().bringIntoViewOnFocus(),
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
            TextButton(onClick = {
                // Sin cambios no se guarda (evita vaciar el catálogo y el aviso de «Servidor guardado»).
                when {
                    url.trim().trimEnd('/') == vm.serverUrl -> onDismiss()
                    vm.saveServer(url) -> onDismiss()
                    else -> invalid = true
                }
            }) { Text("Guardar") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cerrar") } },
    )
}

/**
 * true si la URL usa HTTP sin cifrar. La política de red de la app permite HTTP (servidor en la red local
 * sin certificado), así que en ese caso solo se muestra un aviso.
 */
private fun isCleartext(url: String) = url.trim().startsWith("http://", ignoreCase = true)

/** Aviso de conexión sin cifrar; usa el color terciario (ámbar) porque es una advertencia, no un error. */
@Composable
private fun CleartextWarning() {
    Text(
        "Conexión sin cifrar (HTTP): úsala solo en una red de confianza. En la nube usa https://.",
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.tertiary,
    )
}
