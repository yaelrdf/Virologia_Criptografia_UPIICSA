package mx.ipn.upiicsa.prisma.ui.screens

import androidx.compose.foundation.Image
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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ExitToApp
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.R
import mx.ipn.upiicsa.prisma.ui.PasswordChangeState
import mx.ipn.upiicsa.prisma.ui.components.AdaptivePane
import mx.ipn.upiicsa.prisma.ui.components.CatalogOrGalleryPicker
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.ImagePreview
import mx.ipn.upiicsa.prisma.ui.components.InfoRow
import mx.ipn.upiicsa.prisma.ui.components.LoadingButton
import mx.ipn.upiicsa.prisma.ui.components.PasswordField
import mx.ipn.upiicsa.prisma.ui.components.PngActions
import mx.ipn.upiicsa.prisma.ui.components.SectionCard
import mx.ipn.upiicsa.prisma.ui.components.ThemeModeSelector
import mx.ipn.upiicsa.prisma.ui.components.WarningCard
import mx.ipn.upiicsa.prisma.ui.theme.successColor

@Composable
fun AccountScreen(vm: AppViewModel) {
    val s = vm.account
    val session = vm.session ?: return
    LaunchedEffect(Unit) { vm.refreshAccount() }
    val info = s.info
    val twoFactor = info?.twoFactorEnabled ?: session.twoFactorEnabled

    AdaptivePane(
        title = "Cuenta",
        hasResult = s.keyPng != null,
        form = {
            SectionCard("Perfil") {
                if (s.loadingInfo) LinearProgressIndicator(Modifier.fillMaxWidth())
                InfoRow("Usuario", info?.username ?: session.username)
                InfoRow("Rol", info?.role ?: session.role)
                InfoRow("Doble factor (imagen-llave)", if (twoFactor) "Activado" else "Desactivado")
                info?.createdUtc?.takeIf { it.isNotBlank() }?.let { InfoRow("Creada", formatLocalDateTime(it)) }
                s.error?.let { ErrorCard(it) }
                OutlinedButton(onClick = vm::logout, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ExitToApp, null)
                    Spacer(Modifier.width(8.dp))
                    Text("Cerrar sesión")
                }
            }
            SectionCard("Tema") {
                ThemeModeSelector(vm.themeMode, vm::changeThemeMode)
                Text(
                    "Automático sigue el modo claro u oscuro del teléfono.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            SectionCard("Cambiar contraseña") {
                PasswordChangeForm(vm, s.password, currentLabel = "Contraseña actual", buttonText = "Cambiar contraseña")
                if (s.password.twoFactorDisabled) {
                    WarningCard(
                        "El 2FA se desactivó porque la imagen-llave dependía de la contraseña anterior. " +
                            "Genera otra imagen-llave en «Activar 2FA».",
                    )
                }
            }
            if (!twoFactor) {
                SectionCard("Activar 2FA") {
                    Text(
                        "Elige una imagen; el servidor genera tu imagen-llave (PNG). Al iniciar sesión " +
                            "tendrás que subir ese archivo además de tu contraseña.",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    CatalogOrGalleryPicker(vm, s.keyImage, onSelect = vm::selectKeyImage)
                    PasswordField(value = s.enablePassword, onValueChange = { s.enablePassword = it }, label = "Contraseña actual",
                        imeAction = ImeAction.Done, onDone = vm::enable2fa)
                    LoadingButton(
                        "Generar imagen-llave", s.enabling, onClick = vm::enable2fa,
                        modifier = Modifier.fillMaxWidth(), icon = Icons.Filled.Lock,
                    )
                }
            } else {
                SectionCard("Desactivar 2FA") {
                    Text("Tu cuenta pide la imagen-llave al iniciar sesión.", style = MaterialTheme.typography.bodyMedium,
                        color = successColor, fontWeight = FontWeight.Medium)
                    PasswordField(value = s.disablePassword, onValueChange = { s.disablePassword = it }, label = "Contraseña actual",
                        imeAction = ImeAction.Done, onDone = vm::disable2fa)
                    LoadingButton("Desactivar 2FA", s.disabling, onClick = vm::disable2fa, modifier = Modifier.fillMaxWidth())
                }
            }
        },
        result = {
            val png = s.keyPng
            if (png != null) {
                SectionCard("Tu imagen-llave") {
                    WarningCard(
                        "Guarda este archivo ahora. Sin él no podrás entrar a tu cuenta " +
                            "(solo un administrador puede restablecer el 2FA).",
                    )
                    ImagePreview(s.keyPreview, "Imagen-llave")
                    PngActions(vm, png, "llave_${session.username}.png")
                }
            }
        },
    )
}

/** Campos actual / nueva / confirmar y botón; lo usan Cuenta y la pantalla obligatoria. */
@Composable
private fun PasswordChangeForm(vm: AppViewModel, s: PasswordChangeState, currentLabel: String, buttonText: String) {
    PasswordField(value = s.current, onValueChange = { s.current = it }, label = currentLabel)
    PasswordField(
        value = s.newPassword, onValueChange = { s.newPassword = it }, label = "Nueva contraseña",
        supportingText = "Mínimo 8 caracteres y distinta de la actual",
    )
    PasswordField(
        value = s.confirm, onValueChange = { s.confirm = it }, label = "Confirmar nueva contraseña",
        imeAction = ImeAction.Done, onDone = { vm.changePassword(s) },
    )
    s.error?.let { ErrorCard(it) }
    LoadingButton(
        buttonText, s.loading, onClick = { vm.changePassword(s) },
        modifier = Modifier.fillMaxWidth(), icon = Icons.Filled.Lock,
    )
}

/**
 * Pantalla obligatoria cuando el administrador restableció la contraseña (mustChangePassword):
 * hasta cambiarla, el servidor rechaza las demás rutas.
 */
@Composable
fun ForcedPasswordScreen(vm: AppViewModel) {
    val s = vm.forcedPassword
    val session = vm.session ?: return
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        Column(
            Modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(16.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Column(Modifier.widthIn(max = 520.dp).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Image(painterResource(R.drawable.logo_panda), contentDescription = null, modifier = Modifier.size(56.dp))
                    Column(Modifier.weight(1f)) {
                        Text("Cambia tu contraseña", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
                        Text("Cuenta: ${session.username}", style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                WarningCard(
                    "Un administrador restableció tu contraseña. Para continuar, escribe la contraseña temporal " +
                        "que te dio y elige una nueva.",
                )
                SectionCard {
                    PasswordChangeForm(vm, s, currentLabel = "Contraseña temporal", buttonText = "Cambiar y continuar")
                }
                TextButton(onClick = vm::logout, modifier = Modifier.align(Alignment.CenterHorizontally)) {
                    Text("Cerrar sesión")
                }
            }
        }
    }
}
