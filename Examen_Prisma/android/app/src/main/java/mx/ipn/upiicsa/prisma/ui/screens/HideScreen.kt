package mx.ipn.upiicsa.prisma.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.data.StegoMode
import mx.ipn.upiicsa.prisma.ui.components.AdaptivePane
import mx.ipn.upiicsa.prisma.ui.components.CatalogOrGalleryPicker
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.ImagePreview
import mx.ipn.upiicsa.prisma.ui.components.InfoRow
import mx.ipn.upiicsa.prisma.ui.components.LoadingButton
import mx.ipn.upiicsa.prisma.ui.components.ModeSelector
import mx.ipn.upiicsa.prisma.ui.components.PasswordField
import mx.ipn.upiicsa.prisma.ui.components.PngActions
import mx.ipn.upiicsa.prisma.ui.components.SectionCard
import mx.ipn.upiicsa.prisma.ui.components.bringIntoViewOnFocus
import java.util.Locale

@Composable
fun HideScreen(vm: AppViewModel) {
    val s = vm.hide
    AdaptivePane(
        title = "Ocultar mensaje",
        subtitle = "Elige una portadora, escribe el mensaje y genera un PNG con el mensaje oculto.",
        hasResult = s.result != null || s.loading || s.error != null,
        form = {
            SectionCard("1. Imagen portadora") {
                CatalogOrGalleryPicker(vm, s.image, onSelect = vm::selectCover)
            }
            SectionCard("2. Mensaje y modo") {
                ModeSelector(s.mode) { s.mode = it }
                Text(
                    if (s.mode == StegoMode.PRISMA)
                        "ABCO cifra el mensaje con tu frase y lo dispersa en píxeles pseudoaleatorios."
                    else "La esteganografía clásica escribe los bits en orden en los bits menos significativos, " +
                        "sin clave: cualquiera puede leerlos.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                OutlinedTextField(
                    value = s.message, onValueChange = { s.message = it },
                    label = { Text("Mensaje secreto") },
                    modifier = Modifier.fillMaxWidth().heightIn(min = 120.dp).bringIntoViewOnFocus(),
                )
                if (s.mode == StegoMode.PRISMA) {
                    PasswordField(
                        value = s.passphrase, onValueChange = { s.passphrase = it },
                        label = "Frase (mínimo 4 caracteres)",
                    )
                }
                CapacityInfo(vm)
                LoadingButton(
                    "Ocultar mensaje", s.loading, onClick = vm::embed,
                    modifier = Modifier.fillMaxWidth(), icon = Icons.Filled.Lock,
                )
            }
        },
        result = {
            s.error?.let { ErrorCard(it) }
            val r = s.result
            if (r != null) {
                SectionCard("Resultado") {
                    ImagePreview(s.resultPreview, "Imagen con el mensaje oculto")
                    InfoRow("Modo", s.mode.label)
                    InfoRow("PSNR", r.psnr?.let { formatPsnr(it) } ?: "—")
                    InfoRow("Bytes usados / capacidad", "${r.payloadBytes ?: "—"} / ${r.capacity ?: "—"}")
                    if (r.resized) InfoRow("Portadora", "reducida a ≤ 1280 px")
                    val fileName = remember(r) { "abco_${System.currentTimeMillis() / 1000}.png" }
                    PngActions(vm, r.png, fileName)
                    Text(
                        "Comparte el archivo PNG tal cual: enviarlo «como foto» lo recomprime y destruye el mensaje.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            } else if (s.loading) {
                SectionCard { LinearProgressIndicator(Modifier.fillMaxWidth()); Text("Ocultando…") }
            }
        },
    )
}

@Composable
private fun CapacityInfo(vm: AppViewModel) {
    val s = vm.hide
    val cap = s.capacity
    val used = remember(s.message) { s.message.encodeToByteArray().size }
    when {
        s.capacityLoading -> Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
            Text("Calculando capacidad…", style = MaterialTheme.typography.bodySmall)
        }
        cap != null -> {
            val max = if (s.mode == StegoMode.PRISMA) cap.prismaCapacity else cap.classicCapacity
            val over = used > max
            Text(
                "Mensaje: $used bytes · Capacidad ${s.mode.label}: $max bytes (${cap.width}×${cap.height} px" +
                    (if (cap.resized) ", reducida de ${cap.originalWidth}×${cap.originalHeight}" else "") + ")",
                style = MaterialTheme.typography.bodySmall,
                color = if (over) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            )
            LinearProgressIndicator(
                progress = { if (max > 0) (used.toFloat() / max).coerceIn(0f, 1f) else 0f },
                modifier = Modifier.fillMaxWidth(),
            )
            if (over) Text("El mensaje no cabe en esta imagen.", color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodySmall)
        }
    }
}

private fun formatPsnr(v: Double): String =
    if (v.isInfinite()) "∞ dB (sin cambios)" else String.format(Locale.US, "%.2f dB", v)
