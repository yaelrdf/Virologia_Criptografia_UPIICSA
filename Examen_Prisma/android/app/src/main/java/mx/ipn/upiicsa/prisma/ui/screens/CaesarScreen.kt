package mx.ipn.upiicsa.prisma.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Button
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.CaesarAction
import mx.ipn.upiicsa.prisma.ui.components.AdaptivePane
import mx.ipn.upiicsa.prisma.ui.components.ButtonContent
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.InfoRow
import mx.ipn.upiicsa.prisma.ui.components.SectionCard
import mx.ipn.upiicsa.prisma.ui.theme.MonoStyle
import java.util.Locale

@Composable
fun CaesarScreen(vm: AppViewModel) {
    val s = vm.caesar
    AdaptivePane(
        title = "Cifrado César",
        subtitle = "Desplaza cada letra k posiciones. Se rompe con análisis de frecuencias (χ²).",
        form = {
            SectionCard {
                OutlinedTextField(
                    value = s.text, onValueChange = { s.text = it },
                    label = { Text("Texto") },
                    modifier = Modifier.fillMaxWidth().heightIn(min = 120.dp),
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Incluir Ñ", style = MaterialTheme.typography.bodyLarge)
                        Text(
                            if (s.enye) "Alfabeto de 27 letras (clave 0 a 26)" else "Alfabeto de 26 letras (clave 0 a 25)",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    Switch(checked = s.enye, onCheckedChange = {
                        s.enye = it
                        s.key = s.key.coerceIn(0, s.maxKey)
                    })
                }
                Text("Clave: ${s.key}", style = MaterialTheme.typography.titleMedium)
                Slider(
                    value = s.key.toFloat(),
                    onValueChange = { s.key = it.toInt().coerceIn(0, s.maxKey) },
                    valueRange = 0f..s.maxKey.toFloat(),
                    steps = s.maxKey - 1,
                )
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = { vm.caesarRun(CaesarAction.ENCRYPT) }, enabled = !s.loading,
                        modifier = Modifier.weight(1f)) { Text("Cifrar") }
                    FilledTonalButton(onClick = { vm.caesarRun(CaesarAction.DECRYPT) }, enabled = !s.loading,
                        modifier = Modifier.weight(1f)) { Text("Descifrar") }
                    OutlinedButton(onClick = { vm.caesarRun(CaesarAction.CRACK) }, enabled = !s.loading,
                        modifier = Modifier.weight(1f)) { ButtonContent("Romper", s.loading) }
                }
            }
        },
        result = {
            s.error?.let { ErrorCard(it) }
            s.output?.let { out ->
                SectionCard(s.outputLabel) {
                    SelectionContainer { Text(out, style = MaterialTheme.typography.bodyLarge) }
                    if (s.alphabet.isNotEmpty()) {
                        Text("Alfabeto: ${s.alphabet}", style = MonoStyle, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    TextButton(onClick = { s.text = out; s.output = null }) { Text("Usar como texto de entrada") }
                }
            }
            s.crack?.let { r ->
                SectionCard("Ataque por χ²") {
                    InfoRow("Mejor clave", r.best.key.toString())
                    InfoRow("χ²", fmt(r.best.chiSquare))
                    InfoRow("Tiempo", "${fmt(r.ms)} ms")
                    SelectionContainer {
                        Text(r.best.preview, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
                    }
                    Text("Primeras 5 candidatas", style = MaterialTheme.typography.titleSmall)
                    CandidateTable(r.candidates.take(5))
                    TextButton(onClick = { s.key = r.best.key.coerceIn(0, s.maxKey) }) {
                        Text("Usar clave ${r.best.key} en el control")
                    }
                }
            }
        },
    )
}

@Composable
private fun CandidateTable(rows: List<mx.ipn.upiicsa.prisma.data.CrackCandidate>) {
    Column {
        Row(Modifier.fillMaxWidth().padding(vertical = 4.dp)) {
            Header("Clave", Modifier.weight(0.6f))
            Header("χ²", Modifier.weight(1f))
            Header("Vista previa", Modifier.weight(2.6f))
        }
        HorizontalDivider()
        rows.forEach { c ->
            Row(Modifier.fillMaxWidth().padding(vertical = 6.dp)) {
                Text(c.key.toString(), style = MonoStyle, modifier = Modifier.weight(0.6f))
                Text(fmt(c.chiSquare), style = MonoStyle, modifier = Modifier.weight(1f))
                Text(c.preview, style = MonoStyle, maxLines = 2, overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(2.6f))
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
        }
    }
}

@Composable
private fun Header(text: String, modifier: Modifier) {
    Text(text, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = modifier)
}

private fun fmt(v: Double) = String.format(Locale.US, "%.2f", v)
