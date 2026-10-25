package mx.ipn.upiicsa.prisma.ui.screens

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.data.StegoMode
import mx.ipn.upiicsa.prisma.ui.components.AdaptivePane
import mx.ipn.upiicsa.prisma.ui.components.ErrorCard
import mx.ipn.upiicsa.prisma.ui.components.ImagePreview
import mx.ipn.upiicsa.prisma.ui.components.InfoRow
import mx.ipn.upiicsa.prisma.ui.components.LoadingButton
import mx.ipn.upiicsa.prisma.ui.components.ModeSelector
import mx.ipn.upiicsa.prisma.ui.components.PasswordField
import mx.ipn.upiicsa.prisma.ui.components.SectionCard
import mx.ipn.upiicsa.prisma.ui.theme.MonoStyle

@Composable
fun RevealScreen(vm: AppViewModel) {
    val s = vm.reveal
    // OpenDocument entrega el archivo original (sin recompresión), también desde Descargas.
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) vm.pickStegoImage(uri)
    }
    AdaptivePane(
        title = "Revelar mensaje",
        subtitle = "Elige el PNG recibido y la frase con la que se ocultó el mensaje.",
        hasResult = s.result != null || s.loading || s.error != null,
        form = {
            SectionCard("Imagen") {
                OutlinedButton(onClick = { picker.launch(arrayOf("image/*")) }, modifier = Modifier.fillMaxWidth()) {
                    Text(if (s.imageUri == null) "Elegir imagen" else "Cambiar imagen")
                }
                if (s.imageUri != null) {
                    Text(s.imageName, style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                    ImagePreview(s.preview, "Imagen elegida", maxHeight = 260)
                }
            }
            SectionCard("Modo y frase") {
                ModeSelector(s.mode) { s.mode = it }
                if (s.mode == StegoMode.PRISMA) {
                    PasswordField(
                        value = s.passphrase, onValueChange = { s.passphrase = it }, label = "Frase",
                        imeAction = ImeAction.Done, onDone = vm::extract,
                    )
                }
                LoadingButton(
                    "Revelar", s.loading, onClick = vm::extract,
                    modifier = Modifier.fillMaxWidth(), icon = Icons.Filled.Search,
                )
            }
        },
        result = {
            if (s.loading) SectionCard { LinearProgressIndicator(Modifier.fillMaxWidth()); Text("Leyendo…") }
            s.error?.let { err ->
                ErrorCard(err) {
                    s.rawHex?.let { hex ->
                        Text(
                            "Esto es lo que vería un atacante leyendo los bits menos significativos en orden:",
                            style = MaterialTheme.typography.labelLarge,
                        )
                        SelectionContainer { Text(hex, style = MonoStyle) }
                    }
                }
            }
            s.result?.let { r ->
                SectionCard("Mensaje revelado") {
                    SelectionContainer {
                        Text(r.message, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
                    }
                    InfoRow("Modo", if (r.mode == StegoMode.CLASSIC.apiValue) StegoMode.CLASSIC.label else StegoMode.PRISMA.label)
                    InfoRow("Bytes", r.bytes.toString())
                }
            }
        },
    )
}
