package mx.ipn.upiicsa.prisma.ui.screens

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ExitToApp
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.ui.components.AdaptivePane
import mx.ipn.upiicsa.prisma.ui.components.CatalogOrGalleryPicker
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.ImagePreview
import mx.ipn.upiicsa.prisma.ui.components.InfoRow
import mx.ipn.upiicsa.prisma.ui.components.LoadingButton
import mx.ipn.upiicsa.prisma.ui.components.PasswordField
import mx.ipn.upiicsa.prisma.ui.components.PngActions
import mx.ipn.upiicsa.prisma.ui.components.SectionCard
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
        form = {
            SectionCard("Perfil") {
                if (s.loadingInfo) LinearProgressIndicator(Modifier.fillMaxWidth())
                InfoRow("Usuario", info?.username ?: session.username)
                InfoRow("Rol", info?.role ?: session.role)
                InfoRow("Doble factor (imagen-llave)", if (twoFactor) "Activado" else "Desactivado")
                info?.createdUtc?.takeIf { it.isNotBlank() }?.let { InfoRow("Creada (UTC)", it.take(19).replace('T', ' ')) }
                OutlinedButton(onClick = vm::logout, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ExitToApp, null)
                    Text("  Cerrar sesión")
                }
            }
            s.error?.let { ErrorCard(it) }
            if (!twoFactor) {
                SectionCard("Activar 2FA") {
                    Text(
                        "Elige una imagen; el servidor genera tu imagen-llave (PNG). Al iniciar sesión " +
                            "tendrás que subir ese archivo además de tu contraseña.",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    CatalogOrGalleryPicker(vm, s.keyImage, onSelect = vm::selectKeyImage)
                    PasswordField(value = s.enablePassword, onValueChange = { s.enablePassword = it }, label = "Contraseña actual")
                    LoadingButton(
                        "Generar imagen-llave", s.enabling, onClick = vm::enable2fa,
                        modifier = Modifier.fillMaxWidth(), icon = Icons.Filled.Lock,
                    )
                }
            } else {
                SectionCard("Desactivar 2FA") {
                    Text("Tu cuenta pide la imagen-llave al iniciar sesión.", style = MaterialTheme.typography.bodyMedium,
                        color = successColor, fontWeight = FontWeight.Medium)
                    PasswordField(value = s.disablePassword, onValueChange = { s.disablePassword = it }, label = "Contraseña actual")
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
