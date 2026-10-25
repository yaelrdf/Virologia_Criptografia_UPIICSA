package mx.ipn.upiicsa.prisma.ui.screens

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.PersistableBundle
import androidx.compose.foundation.Image
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.AssistChipDefaults
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.data.AdminUser
import mx.ipn.upiicsa.prisma.data.AuditEntry
import mx.ipn.upiicsa.prisma.data.StegoMode
import mx.ipn.upiicsa.prisma.ui.ShieldIcon
import mx.ipn.upiicsa.prisma.ui.components.AdaptivePane
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.SectionCard
import mx.ipn.upiicsa.prisma.ui.theme.MonoStyle
import mx.ipn.upiicsa.prisma.ui.theme.successColor
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Acción pendiente de confirmar sobre un usuario. */
private sealed interface PendingAction {
    val user: AdminUser
    data class ResetPassword(override val user: AdminUser) : PendingAction
    data class ChangeRole(override val user: AdminUser, val makeAdmin: Boolean) : PendingAction
    data class Reset2fa(override val user: AdminUser) : PendingAction
}

/**
 * Administración (rol admin): usuarios, auditoría y traza de la última operación ABCO.
 * En pantallas anchas, usuarios a la izquierda y auditoría + traza a la derecha (AdaptivePane).
 */
@Composable
fun AdminScreen(vm: AppViewModel) {
    val s = vm.admin
    LaunchedEffect(Unit) { if (!s.loaded) vm.refreshAdmin() }
    var pending by remember { mutableStateOf<PendingAction?>(null) }

    AdaptivePane(
        title = "Administración",
        subtitle = "Usuarios, auditoría y traza de la última operación ABCO.",
        hasResult = true,
        headerActions = {
            FilledTonalButton(onClick = vm::refreshAdmin, enabled = !s.loading) {
                if (s.loading) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                else Icon(Icons.Filled.Refresh, null, Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
                Text("Actualizar")
            }
        },
        form = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("Usuarios (${s.users.size})", style = MaterialTheme.typography.titleMedium)
                if (s.loading && !s.loaded) LinearProgressIndicator(Modifier.fillMaxWidth())
                s.error?.let { ErrorCard(it) }
                s.users.forEach { user ->
                    UserCard(
                        vm, user,
                        busy = user.id in s.busy,
                        onAction = { pending = it },
                    )
                }
            }
        },
        result = {
            AuditCard(s.audit, loading = s.loading && !s.loaded)
            TraceCard(vm)
        },
    )

    pending?.let { action ->
        ConfirmDialog(
            action = action,
            name = vm.displayName(action.user),
            onConfirm = {
                pending = null
                when (action) {
                    is PendingAction.ResetPassword -> vm.resetUserPassword(action.user)
                    is PendingAction.ChangeRole -> vm.setUserRole(action.user, action.makeAdmin)
                    is PendingAction.Reset2fa -> vm.resetUser2fa(action.user)
                }
            },
            onDismiss = { pending = null },
        )
    }
    s.temporaryPassword?.let { (name, password) ->
        TemporaryPasswordDialog(name, password, onDismiss = vm::dismissTemporaryPassword)
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun UserCard(
    vm: AppViewModel,
    user: AdminUser,
    busy: Boolean,
    onAction: (PendingAction) -> Unit,
) {
    val revealed = vm.admin.revealed[user.id]
    val isAdmin = user.role == "admin"
    SectionCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                SelectionContainer {
                    Text(
                        revealed ?: user.username,
                        style = MaterialTheme.typography.titleMedium,
                        fontFamily = if (revealed == null) FontFamily.Monospace else null,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 2, overflow = TextOverflow.Ellipsis,
                    )
                }
                Text(
                    if (revealed == null) "Nombre ofuscado (César) · id ${user.id}" else "Nombre real · id ${user.id}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            TextButton(onClick = { vm.toggleReveal(user) }, enabled = !busy) {
                Text(if (revealed == null) "Revelar" else "Ocultar")
            }
        }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            StatusChip(if (isAdmin) "Administrador" else "Usuario", highlighted = isAdmin)
            StatusChip(if (user.twoFactorEnabled) "2FA activado" else "Sin 2FA", highlighted = user.twoFactorEnabled)
            if (user.mustChangePassword) StatusChip("Contraseña temporal pendiente", warning = true)
        }
        if (user.createdUtc.isNotBlank()) {
            Text("Creado: ${formatLocalDateTime(user.createdUtc)}", style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            OutlinedButton(onClick = { onAction(PendingAction.ResetPassword(user)) }, enabled = !busy) {
                Text("Restablecer contraseña")
            }
            OutlinedButton(onClick = { onAction(PendingAction.ChangeRole(user, makeAdmin = !isAdmin)) }, enabled = !busy) {
                Text(if (isAdmin) "Quitar administrador" else "Hacer administrador")
            }
            if (user.twoFactorEnabled) {
                OutlinedButton(onClick = { onAction(PendingAction.Reset2fa(user)) }, enabled = !busy) {
                    Text("Restablecer 2FA")
                }
            }
        }
    }
}

@Composable
private fun StatusChip(text: String, highlighted: Boolean = false, warning: Boolean = false) {
    val color = when {
        warning -> MaterialTheme.colorScheme.tertiary
        highlighted -> successColor
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    AssistChip(
        onClick = {},
        label = { Text(text) },
        leadingIcon = if (warning) {
            { Icon(Icons.Filled.Warning, null, Modifier.size(16.dp)) }
        } else null,
        colors = AssistChipDefaults.assistChipColors(labelColor = color, leadingIconContentColor = color),
    )
}

@Composable
private fun ConfirmDialog(action: PendingAction, name: String, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    val (title, body, confirm) = when (action) {
        is PendingAction.ResetPassword -> Triple(
            "¿Restablecer contraseña?",
            "Se generará una contraseña temporal para «$name». Su 2FA se desactivará y deberá cambiar " +
                "la contraseña al iniciar sesión.",
            "Restablecer",
        )
        is PendingAction.ChangeRole -> if (action.makeAdmin) Triple(
            "¿Hacer administrador?",
            "«$name» podrá ver la auditoría, la traza y administrar usuarios.",
            "Hacer administrador",
        ) else Triple(
            "¿Quitar administrador?",
            "«$name» pasará a ser usuario normal.",
            "Quitar administrador",
        )
        is PendingAction.Reset2fa -> Triple(
            "¿Restablecer 2FA?",
            "«$name» podrá entrar solo con su contraseña y tendrá que generar otra imagen-llave.",
            "Restablecer 2FA",
        )
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(ShieldIcon, null) },
        title = { Text(title) },
        text = { Text(body) },
        confirmButton = { TextButton(onClick = onConfirm) { Text(confirm) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancelar") } },
    )
}

@Composable
private fun TemporaryPasswordDialog(name: String, password: String, onDismiss: () -> Unit) {
    val context = LocalContext.current
    var copied by remember { mutableStateOf(false) }
    AlertDialog(
        // Solo se cierra con el botón: la contraseña no se vuelve a mostrar.
        onDismissRequest = {},
        icon = { Icon(ShieldIcon, null) },
        title = { Text("Contraseña temporal") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("Usuario: $name", style = MaterialTheme.typography.bodyMedium)
                Surface(
                    color = MaterialTheme.colorScheme.surfaceContainerHighest,
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    SelectionContainer {
                        Text(
                            password,
                            fontFamily = FontFamily.Monospace,
                            fontSize = 26.sp,
                            lineHeight = 32.sp,
                            fontWeight = FontWeight.Bold,
                            modifier = Modifier.padding(16.dp),
                        )
                    }
                }
                Button(
                    onClick = { copyToClipboard(context, password); copied = true },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text(if (copied) "Copiada" else "Copiar") }
                Text(
                    "Se muestra una sola vez; el 2FA del usuario se desactivó.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.tertiary,
                )
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Listo") } },
    )
}

/** Copia marcando el contenido como sensible (Android 13+ no lo muestra en la vista previa). */
private fun copyToClipboard(context: Context, text: String) {
    val clip = ClipData.newPlainText("Contraseña temporal", text)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        clip.description.extras = PersistableBundle().apply { putBoolean(ClipDescription.EXTRA_IS_SENSITIVE, true) }
    }
    context.getSystemService(ClipboardManager::class.java)?.setPrimaryClip(clip)
}

@Composable
private fun AuditCard(entries: List<AuditEntry>, loading: Boolean) {
    SectionCard("Auditoría (últimas ${entries.size})") {
        if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        if (entries.isEmpty() && !loading) {
            Text("Sin registros.", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        entries.forEachIndexed { i, e ->
            if (i > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
            AuditRow(e)
        }
    }
}

@Composable
private fun AuditRow(e: AuditEntry) {
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                e.action.ifBlank { "—" }, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium,
                modifier = Modifier.weight(1f), maxLines = 2, overflow = TextOverflow.Ellipsis,
            )
            Text(
                if (e.ok) "ok" else "falló",
                style = MaterialTheme.typography.labelLarge,
                fontWeight = FontWeight.Bold,
                color = if (e.ok) successColor else MaterialTheme.colorScheme.error,
            )
        }
        Text(
            formatLocalDateTime(e.utc) + " · " + (e.user ?: "—"),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 2, overflow = TextOverflow.Ellipsis,
        )
        val details = buildList {
            e.mode?.takeIf { it.isNotBlank() }?.let { add("modo ${modeLabel(it)}") }
            add("${e.bytes} B")
            add("${String.format(Locale.US, "%.0f", e.ms)} ms")
        }.joinToString(" · ")
        Text(details, style = MonoStyle.copy(fontSize = 12.sp), color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

private fun modeLabel(mode: String) = StegoMode.entries.firstOrNull { it.apiValue == mode }?.label ?: mode

@Composable
private fun TraceCard(vm: AppViewModel) {
    val s = vm.admin
    val trace = s.trace
    SectionCard("Traza de la última operación ABCO") {
        when {
            trace == null && s.loaded -> Text(
                "Aún no hay operaciones ABCO.",
                style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            trace != null -> {
                trace.user?.let {
                    Text("Usuario (ofuscado): $it", style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Surface(
                    color = MaterialTheme.colorScheme.surfaceContainerHighest,
                    shape = RoundedCornerShape(8.dp),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    SelectionContainer {
                        Text(
                            trace.text,
                            style = MonoStyle.copy(fontSize = 12.sp, lineHeight = 17.sp),
                            softWrap = false,
                            modifier = Modifier.horizontalScroll(rememberScrollState()).padding(12.dp),
                        )
                    }
                }
            }
        }
        s.diffMap?.let { map ->
            Text("Mapa de diferencias |original − estego| (amplificado)", style = MaterialTheme.typography.titleSmall)
            Image(
                bitmap = map,
                contentDescription = "Mapa de diferencias",
                contentScale = ContentScale.Fit,
                filterQuality = FilterQuality.None,
                modifier = Modifier.fillMaxWidth().heightIn(max = 360.dp).clip(RoundedCornerShape(8.dp)),
            )
        }
    }
}

private val localFormat: DateTimeFormatter = DateTimeFormatter.ofPattern("dd/MM/yyyy HH:mm:ss", Locale.forLanguageTag("es-MX"))

/** Convierte una fecha UTC del servidor (con o sin «Z») a la hora local del teléfono. */
fun formatLocalDateTime(utc: String): String = runCatching {
    val instant = runCatching { OffsetDateTime.parse(utc).toInstant() }
        .getOrElse { LocalDateTime.parse(utc).toInstant(ZoneOffset.UTC) }
    localFormat.format(instant.atZone(ZoneId.systemDefault()))
}.getOrDefault(utc.take(19).replace('T', ' '))
