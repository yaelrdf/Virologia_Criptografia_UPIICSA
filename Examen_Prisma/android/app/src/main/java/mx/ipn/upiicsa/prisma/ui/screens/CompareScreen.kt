package mx.ipn.upiicsa.prisma.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.AssistChip
import androidx.compose.material3.AssistChipDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.data.CompareResult
import mx.ipn.upiicsa.prisma.ui.components.AdaptivePane
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.InfoRow
import mx.ipn.upiicsa.prisma.ui.components.LoadingButton
import mx.ipn.upiicsa.prisma.ui.components.PasswordField
import mx.ipn.upiicsa.prisma.ui.components.SectionCard
import mx.ipn.upiicsa.prisma.ui.theme.MonoStyle
import mx.ipn.upiicsa.prisma.ui.theme.successColor
import java.util.Locale

@Composable
fun CompareScreen(vm: AppViewModel) {
    val s = vm.compare
    AdaptivePane(
        title = "Comparador",
        subtitle = "El mismo mensaje con César, LSB clásico, PRISMA-L y AES-256-GCM.",
        form = {
            SectionCard {
                OutlinedTextField(
                    value = s.message, onValueChange = { s.message = it },
                    label = { Text("Mensaje") },
                    modifier = Modifier.fillMaxWidth().heightIn(min = 100.dp),
                )
                PasswordField(value = s.passphrase, onValueChange = { s.passphrase = it }, label = "Frase (mínimo 4)")
                Text("Clave César: ${s.caesarKey}", style = MaterialTheme.typography.titleMedium)
                Slider(
                    value = s.caesarKey.toFloat(),
                    onValueChange = { s.caesarKey = it.toInt() },
                    valueRange = 0f..25f,
                    steps = 24,
                )
                LoadingButton("Comparar", s.loading, onClick = vm::runCompare, modifier = Modifier.fillMaxWidth())
            }
        },
        result = {
            s.error?.let { ErrorCard(it) }
            if (s.loading && s.result == null) SectionCard { LinearProgressIndicator(Modifier.fillMaxWidth()) }
            s.result?.let { r ->
                SectionCard("Texto plano") {
                    InfoRow("Bytes", r.plainBytes.toString())
                    EntropyBar(r.plainEntropy)
                }
                r.results.forEach { AlgorithmCard(it) }
            }
        },
    )
}

@Composable
private fun AlgorithmCard(r: CompareResult) {
    SectionCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(r.algorithm, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(r.kind, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            val ok = r.detectsTampering
            AssistChip(
                onClick = {},
                label = { Text(if (ok) "Detecta alteración" else "No detecta alteración") },
                colors = AssistChipDefaults.assistChipColors(
                    labelColor = if (ok) successColor else MaterialTheme.colorScheme.error,
                ),
            )
        }
        InfoRow("Bytes de salida", r.outputBytes.toString())
        InfoRow("Tiempo", "${String.format(Locale.US, "%.2f", r.ms)} ms")
        EntropyBar(r.entropy)
        if (r.notes.isNotBlank()) Text(r.notes, style = MaterialTheme.typography.bodyMedium)
        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text("Salida (hex)", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            SelectionContainer { Text(r.outputHex, style = MonoStyle) }
        }
    }
}

/** Entropía de Shannon en bits por byte (0 a 8). */
@Composable
private fun EntropyBar(entropy: Double) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        InfoRow("Entropía", "${String.format(Locale.US, "%.3f", entropy)} / 8 bits por byte")
        LinearProgressIndicator(
            progress = { (entropy / 8.0).toFloat().coerceIn(0f, 1f) },
            modifier = Modifier.fillMaxWidth(),
        )
    }
}
