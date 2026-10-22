package mx.ipn.upiicsa.prisma.ui.components

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import mx.ipn.upiicsa.prisma.data.StegoMode

/** true cuando la ventana mide ≥ 600 dp de ancho (tableta, plegable, horizontal). */
val LocalWideLayout = compositionLocalOf { false }

/**
 * Pantalla con formulario y resultado: lado a lado en pantallas anchas,
 * una sola columna con scroll en teléfono. El contenido tiene un ancho máximo.
 */
@Composable
fun AdaptivePane(
    title: String,
    subtitle: String? = null,
    form: @Composable ColumnScope.() -> Unit,
    result: @Composable ColumnScope.() -> Unit,
) {
    val wide = LocalWideLayout.current
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        if (wide) {
            Row(
                Modifier.widthIn(max = 1200.dp).fillMaxSize().padding(horizontal = 24.dp),
                horizontalArrangement = Arrangement.spacedBy(24.dp),
            ) {
                Column(
                    Modifier.weight(1f).fillMaxSize().verticalScroll(rememberScrollState()).padding(vertical = 24.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    ScreenHeader(title, subtitle)
                    form()
                }
                Column(
                    Modifier.weight(1f).fillMaxSize().verticalScroll(rememberScrollState()).padding(vertical = 24.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                    content = result,
                )
            }
        } else {
            Column(
                Modifier.widthIn(max = 640.dp).fillMaxSize().verticalScroll(rememberScrollState())
                    .padding(horizontal = 16.dp, vertical = 16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                ScreenHeader(title, subtitle)
                form()
                result()
                Spacer(Modifier.size(8.dp))
            }
        }
    }
}

@Composable
fun ScreenHeader(title: String, subtitle: String?) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(title, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
        if (subtitle != null) {
            Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
fun SectionCard(
    title: String? = null,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    ElevatedCard(modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            if (title != null) Text(title, style = MaterialTheme.typography.titleMedium)
            content()
        }
    }
}

@Composable
fun ErrorCard(message: String, extra: (@Composable ColumnScope.() -> Unit)? = null) {
    Card(
        Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.errorContainer,
            contentColor = MaterialTheme.colorScheme.onErrorContainer,
        ),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(Icons.Filled.Warning, contentDescription = null)
                Text(message, style = MaterialTheme.typography.bodyMedium)
            }
            extra?.invoke(this)
        }
    }
}

/** Aviso destacado (no error), p. ej. para recordar guardar la imagen-llave. */
@Composable
fun WarningCard(message: String) {
    Card(
        Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.tertiaryContainer,
            contentColor = MaterialTheme.colorScheme.onTertiaryContainer,
        ),
    ) {
        Row(Modifier.padding(16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Icon(Icons.Filled.Warning, contentDescription = null)
            Text(message, style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@Composable
fun LoadingButton(
    text: String,
    loading: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    icon: ImageVector? = null,
) {
    Button(onClick = onClick, enabled = enabled && !loading, modifier = modifier) {
        ButtonContent(text, loading, icon)
    }
}

@Composable
fun RowScope.ButtonContent(text: String, loading: Boolean, icon: ImageVector? = null) {
    when {
        loading -> CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
        icon != null -> Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp))
    }
    if (loading || icon != null) Spacer(Modifier.width(8.dp))
    Text(text)
}

@Composable
fun PasswordField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    modifier: Modifier = Modifier,
    supportingText: String? = null,
    imeAction: ImeAction = ImeAction.Next,
    onDone: (() -> Unit)? = null,
) {
    var visible by rememberSaveable { mutableStateOf(false) }
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        singleLine = true,
        modifier = modifier.fillMaxWidth(),
        visualTransformation = if (visible) VisualTransformation.None else PasswordVisualTransformation(),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = imeAction),
        keyboardActions = androidx.compose.foundation.text.KeyboardActions(onDone = { onDone?.invoke() }),
        supportingText = supportingText?.let { { Text(it) } },
        trailingIcon = {
            TextButton(onClick = { visible = !visible }) { Text(if (visible) "Ocultar" else "Ver") }
        },
    )
}

@Composable
fun ModeSelector(mode: StegoMode, onChange: (StegoMode) -> Unit) {
    val modes = StegoMode.entries
    SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
        modes.forEachIndexed { index, m ->
            SegmentedButton(
                selected = mode == m,
                onClick = { onChange(m) },
                shape = SegmentedButtonDefaults.itemShape(index = index, count = modes.size),
            ) { Text(m.label) }
        }
    }
}

@Composable
fun ImagePreview(bitmap: ImageBitmap?, description: String, modifier: Modifier = Modifier, maxHeight: Int = 360) {
    Box(
        modifier.fillMaxWidth().heightIn(min = 120.dp, max = maxHeight.dp).clip(RoundedCornerShape(12.dp)),
        contentAlignment = Alignment.Center,
    ) {
        if (bitmap == null) {
            CircularProgressIndicator()
        } else {
            Image(
                bitmap = bitmap,
                contentDescription = description,
                contentScale = ContentScale.Fit,
                modifier = Modifier.fillMaxWidth().heightIn(max = maxHeight.dp),
            )
        }
    }
}

/** Fila "etiqueta: valor" para resultados. */
@Composable
fun InfoRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(12.dp))
        SelectionContainer { Text(value, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium) }
    }
}
