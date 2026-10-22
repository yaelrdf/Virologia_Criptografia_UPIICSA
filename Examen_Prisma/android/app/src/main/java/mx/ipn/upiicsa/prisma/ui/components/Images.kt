package mx.ipn.upiicsa.prisma.ui.components

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import mx.ipn.upiicsa.prisma.AppViewModel
import mx.ipn.upiicsa.prisma.data.Files
import mx.ipn.upiicsa.prisma.data.ImageSource
import mx.ipn.upiicsa.prisma.ui.PickedImage

/**
 * Selector de imagen: rejilla con las portadoras del catálogo del servidor
 * o una imagen de la galería (Photo Picker), con vista previa de la elegida.
 */
@Composable
fun CatalogOrGalleryPicker(vm: AppViewModel, picked: PickedImage, onSelect: (ImageSource) -> Unit) {
    LaunchedEffect(Unit) { vm.loadCatalog() }
    val gallery = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) onSelect(vm.localSource(uri))
    }
    val selected = picked.source
    val columns = 3

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        when {
            vm.catalog.isEmpty() && vm.catalogLoading ->
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    Text("Cargando catálogo…")
                }
            vm.catalog.isEmpty() && vm.catalogError != null ->
                ErrorCard(vm.catalogError ?: "") {
                    TextButton(onClick = { vm.loadCatalog(force = true) }) {
                        Icon(Icons.Filled.Refresh, null); Spacer(Modifier.size(6.dp)); Text("Reintentar")
                    }
                }
        }
        // Rejilla sin LazyGrid (vive dentro de una columna con scroll).
        vm.catalog.chunked(columns).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                row.forEach { item ->
                    val isSelected = selected is ImageSource.Catalog && selected.item.id == item.id
                    Card(
                        onClick = { onSelect(ImageSource.Catalog(item)) },
                        modifier = Modifier.weight(1f),
                        border = if (isSelected) BorderStroke(3.dp, MaterialTheme.colorScheme.primary) else null,
                        colors = CardDefaults.cardColors(
                            containerColor = if (isSelected) MaterialTheme.colorScheme.primaryContainer
                            else MaterialTheme.colorScheme.surfaceContainerHigh,
                        ),
                    ) {
                        Box(Modifier.fillMaxWidth().aspectRatio(4f / 3f), contentAlignment = Alignment.Center) {
                            val thumb = vm.thumbs[item.id]
                            if (thumb != null) {
                                Image(thumb, item.name, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
                            } else {
                                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                            }
                            if (isSelected) {
                                Icon(
                                    Icons.Filled.Check, contentDescription = "Seleccionada",
                                    tint = MaterialTheme.colorScheme.onPrimary,
                                    modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(22.dp),
                                )
                            }
                        }
                        Text(
                            item.name, maxLines = 1, overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.labelMedium,
                            modifier = Modifier.padding(horizontal = 8.dp, vertical = 6.dp),
                        )
                    }
                }
                repeat(columns - row.size) { Spacer(Modifier.weight(1f)) }
            }
        }
        OutlinedButton(
            onClick = { gallery.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
            modifier = Modifier.fillMaxWidth(),
        ) {
            Icon(Icons.Filled.Add, null); Spacer(Modifier.size(8.dp)); Text("Elegir de la galería")
        }
        if (selected is ImageSource.Local) {
            Text(
                "Imagen elegida: ${selected.displayName}",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            ImagePreview(picked.preview, "Imagen elegida", maxHeight = 240)
        }
    }
}

/**
 * Botones "Guardar" (CreateDocument) y "Compartir como archivo" (FileProvider + ACTION_SEND)
 * para un PNG generado por el servidor. Antes de compartir avisa de no enviarlo "como foto".
 */
@Composable
fun PngActions(vm: AppViewModel, png: ByteArray, fileName: String) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var askShare by rememberSaveable { mutableStateOf(false) }
    val currentPng = remember(png) { png }
    val saver = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("image/png")) { uri ->
        if (uri != null) vm.savePng(uri, currentPng)
    }

    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Button(onClick = { saver.launch(fileName) }, modifier = Modifier.weight(1f)) {
            Icon(Icons.Filled.Check, null, Modifier.size(18.dp)); Spacer(Modifier.size(8.dp)); Text("Guardar")
        }
        OutlinedButton(onClick = { askShare = true }, modifier = Modifier.weight(1f)) {
            Icon(Icons.Filled.Share, null, Modifier.size(18.dp)); Spacer(Modifier.size(8.dp))
            Text("Compartir como archivo", maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }

    if (askShare) {
        AlertDialog(
            onDismissRequest = { askShare = false },
            icon = { Icon(Icons.Filled.Share, null) },
            title = { Text("Compártela como archivo") },
            text = {
                Text(
                    "Envía el PNG como archivo o documento (por ejemplo, «Documento» en WhatsApp o adjunto " +
                        "de correo). Si lo envías «como foto», la app lo recomprime y el mensaje oculto se destruye.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    askShare = false
                    scope.launch {
                        try {
                            Files.sharePng(context, currentPng, fileName)
                        } catch (e: Exception) {
                            vm.notify("No se pudo compartir: ${e.message}")
                        }
                    }
                }) { Text("Entendido, compartir") }
            },
            dismissButton = { TextButton(onClick = { askShare = false }) { Text("Cancelar") } },
        )
    }
}
