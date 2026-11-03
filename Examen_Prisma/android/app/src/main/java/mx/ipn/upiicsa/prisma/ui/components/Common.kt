// Componentes Compose reutilizables de ABCO: AdaptivePane (1 o 2 columnas), distribución tipo masonry,
// tarjetas, botones con progreso, campos de contraseña, selectores segmentados y ayuda para el teclado.
package mx.ipn.upiicsa.prisma.ui.components

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.TextAutoSize
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
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import mx.ipn.upiicsa.prisma.data.StegoMode
import mx.ipn.upiicsa.prisma.data.ThemeMode

/**
 * true cuando la ventana mide ≥ 600 dp de ancho (tableta, plegable, horizontal).
 * Lo publica PrismaApp; el valor por defecto (false) aplica a vistas previas y pruebas.
 */
val LocalWideLayout = compositionLocalOf { false }

/** Ancho máximo del contenido en pantallas grandes, para que las líneas de texto no sean demasiado largas. */
val MaxContentWidth = 1200.dp

/**
 * Ancho disponible (del panel, no de la ventana) a partir del cual se usan 2 columnas. Se mide el panel
 * porque con el NavigationRail el espacio útil es menor que la ventana; 560 dp dejan ≈ 270 dp por columna.
 */
val TwoColumnMinWidth = 560.dp

/**
 * Pantalla con formulario y resultado.
 *
 * - Panel angosto (< [TwoColumnMinWidth]): una sola columna con scroll (formulario y luego resultado).
 * - Panel ancho sin resultado: el formulario ocupa todo el ancho y sus tarjetas fluyen en 2 columnas.
 * - Panel ancho con resultado: formulario | resultado, cada uno con su propio scroll.
 *
 * Cada elemento de [form] (normalmente una [SectionCard]) es una pieza que se reparte entre las columnas.
 * Los contenedores con scroll llevan imePadding() para que el teclado no tape los campos.
 *
 * @param title título de la pantalla.
 * @param subtitle descripción breve debajo del título.
 * @param hasResult hay un resultado que mostrar; en panel ancho cambia la distribución a formulario | resultado.
 * @param headerActions botones opcionales a la derecha del título (p. ej. «Actualizar»).
 * @param form tarjetas del formulario.
 * @param result contenido del resultado (errores incluidos).
 */
@Composable
fun AdaptivePane(
    title: String,
    subtitle: String? = null,
    hasResult: Boolean,
    headerActions: (@Composable RowScope.() -> Unit)? = null,
    form: @Composable () -> Unit,
    result: @Composable ColumnScope.() -> Unit,
) {
    BoxWithConstraints(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        // Se mide el ancho del panel (ya descontado el NavigationRail), no el de la ventana.
        val wide = maxWidth >= TwoColumnMinWidth
        when {
            !wide -> Column(
                // 640 dp de ancho máximo: en un panel angosto pero no tan pequeño las tarjetas no se estiran.
                // imePadding antes de verticalScroll: el área con scroll se acorta por encima del teclado y
                // así se puede desplazar hasta el último campo.
                Modifier.widthIn(max = 640.dp).fillMaxSize().imePadding()
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 16.dp, vertical = 16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                ScreenHeader(title, subtitle, headerActions)
                form()
                result()
                Spacer(Modifier.size(8.dp))
            }
            !hasResult -> Column(
                Modifier.widthIn(max = MaxContentWidth).fillMaxSize().imePadding()
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 24.dp, vertical = 24.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                ScreenHeader(title, subtitle, headerActions)
                MasonryColumns(columns = 2, spacing = 16.dp) { form() }
                // Por si el resultado tiene algo que mostrar sin marcarse como resultado.
                result()
            }
            else -> Row(
                // En dos columnas cada una tiene su propio scroll e imePadding, para desplazar el formulario
                // sin mover el resultado.
                Modifier.widthIn(max = MaxContentWidth).fillMaxSize().padding(horizontal = 24.dp),
                horizontalArrangement = Arrangement.spacedBy(24.dp),
            ) {
                Column(
                    Modifier.weight(1f).fillMaxSize().imePadding()
                        .verticalScroll(rememberScrollState()).padding(vertical = 24.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    ScreenHeader(title, subtitle, headerActions)
                    form()
                }
                Column(
                    Modifier.weight(1f).fillMaxSize().imePadding()
                        .verticalScroll(rememberScrollState()).padding(vertical = 24.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                    content = result,
                )
            }
        }
    }
}

/**
 * Reparte sus hijos en [columns] columnas de igual ancho, colocando cada uno en la columna
 * más baja (tipo "masonry"); así el orden de lectura se conserva (1.º a la izquierda, 2.º a la derecha)
 * y las columnas quedan equilibradas. Con un solo hijo, este ocupa todo el ancho.
 *
 * Se usa un Layout propio porque Row con dos Column obligaría a decidir de antemano qué tarjeta va en cada
 * columna, y LazyVerticalStaggeredGrid no puede ir dentro de un contenedor con verticalScroll.
 *
 * @param columns número de columnas.
 * @param spacing separación horizontal y vertical entre hijos.
 */
@Composable
fun MasonryColumns(
    modifier: Modifier = Modifier,
    columns: Int = 2,
    spacing: Dp = 16.dp,
    content: @Composable () -> Unit,
) {
    Layout(content = content, modifier = modifier.fillMaxWidth()) { measurables, constraints ->
        val width = constraints.maxWidth
        val gap = spacing.roundToPx()
        if (measurables.size <= 1) {
            val p = measurables.firstOrNull()?.measure(Constraints(minWidth = width, maxWidth = width))
            return@Layout layout(width, p?.height ?: 0) { p?.placeRelative(0, 0) }
        }
        // Ancho de columna: el total menos los espacios entre columnas, repartido por igual.
        val colWidth = ((width - gap * (columns - 1)) / columns).coerceAtLeast(0)
        val heights = IntArray(columns)
        // Cada hijo va a la columna con menor altura acumulada; en empate, la de más a la izquierda.
        val placed = measurables.map { m ->
            val p = m.measure(Constraints(minWidth = colWidth, maxWidth = colWidth))
            val col = heights.indices.minBy { heights[it] }
            val y = heights[col]
            heights[col] += p.height + gap
            Triple(p, col, y)
        }
        // Se resta el último espacio, que queda debajo del hijo más bajo de la columna más alta.
        val height = (heights.maxOrNull() ?: 0) - gap
        layout(width, height.coerceAtLeast(0)) {
            placed.forEach { (p, col, y) -> p.placeRelative(col * (colWidth + gap), y) }
        }
    }
}

/**
 * Encabezado de pantalla: título, subtítulo opcional y acciones a la derecha.
 *
 * @param actions botones opcionales; el título ocupa el resto del ancho.
 */
@Composable
fun ScreenHeader(title: String, subtitle: String?, actions: (@Composable RowScope.() -> Unit)? = null) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(title, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
            if (subtitle != null) {
                Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        actions?.invoke(this)
    }
}

/**
 * Tarjeta de sección del formulario o del resultado; es la pieza que AdaptivePane reparte entre columnas.
 *
 * @param title título opcional de la sección.
 */
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

/**
 * Tarjeta de error con los colores de error del tema.
 *
 * @param message mensaje en español (normalmente el de [mx.ipn.upiicsa.prisma.data.ApiException]).
 * @param extra contenido adicional, p. ej. los bytes en hex que vería un atacante en Revelar.
 */
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

/**
 * Botón principal que muestra un indicador de progreso y se deshabilita mientras [loading] es true,
 * para evitar enviar la misma petición dos veces.
 */
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

/** Contenido de un botón: indicador de progreso o ícono opcional, y texto en una línea. */
@Composable
fun RowScope.ButtonContent(text: String, loading: Boolean, icon: ImageVector? = null) {
    when {
        loading -> CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
        icon != null -> Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp))
    }
    if (loading || icon != null) Spacer(Modifier.width(8.dp))
    Text(text, maxLines = 1, overflow = TextOverflow.Ellipsis)
}

/**
 * Al recibir el foco, y cada vez que cambia la altura del teclado mientras tiene el foco,
 * desplaza el contenedor con scroll para que el campo quede a la vista. Con enableEdgeToEdge
 * la ventana ya no se redimensiona (adjustResize) y el teclado aparece después del foco, por eso
 * se espera a que su animación se asiente.
 *
 * Debe aplicarse dentro de un contenedor con verticalScroll e imePadding (como AdaptivePane).
 */
@OptIn(ExperimentalFoundationApi::class)
fun Modifier.bringIntoViewOnFocus(): Modifier = composed {
    val requester = remember { BringIntoViewRequester() }
    var focused by remember { mutableStateOf(false) }
    val density = LocalDensity.current
    val ime = WindowInsets.ime
    LaunchedEffect(focused) {
        if (!focused) return@LaunchedEffect
        // collectLatest: cada cambio de altura del teclado durante su animación cancela la espera anterior,
        // así el desplazamiento se hace una sola vez, con la altura final.
        snapshotFlow { ime.getBottom(density) }.collectLatest {
            delay(120)
            requester.bringIntoView()
        }
    }
    this.bringIntoViewRequester(requester).onFocusChanged { focused = it.isFocused }
}

/**
 * Campo de contraseña o frase: oculto por defecto, con botón para mostrarlo.
 *
 * @param imeAction acción de la tecla Intro del teclado (siguiente campo o «Listo»).
 * @param onDone se invoca con la acción «Listo» del teclado, p. ej. para enviar el formulario.
 */
@Composable
fun PasswordField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    modifier: Modifier = Modifier,
    supportingText: String? = null,
    imeAction: ImeAction = ImeAction.Next,
    onDone: (() -> Unit)? = null,
    isError: Boolean = false,
) {
    // rememberSaveable: la visibilidad se conserva al rotar. Cada campo tiene la suya.
    var visible by rememberSaveable { mutableStateOf(false) }
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        singleLine = true,
        isError = isError,
        modifier = modifier.fillMaxWidth().bringIntoViewOnFocus(),
        visualTransformation = if (visible) VisualTransformation.None else PasswordVisualTransformation(),
        // KeyboardType.Password evita sugerencias y que el teclado aprenda la contraseña; sin autocorrección.
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = imeAction, autoCorrectEnabled = false),
        keyboardActions = KeyboardActions(onDone = { onDone?.invoke() }),
        supportingText = supportingText?.let { { Text(it) } },
        trailingIcon = {
            TextButton(onClick = { visible = !visible }) { Text(if (visible) "Ocultar" else "Ver") }
        },
    )
}

/** Selector segmentado del modo de ocultamiento: ABCO o esteganografía clásica. */
@Composable
fun ModeSelector(mode: StegoMode, onChange: (StegoMode) -> Unit) {
    val modes = StegoMode.entries
    SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
        modes.forEachIndexed { index, m ->
            SegmentedButton(
                selected = mode == m,
                onClick = { onChange(m) },
                shape = SegmentedButtonDefaults.itemShape(index = index, count = modes.size),
                icon = {},
            ) { SegmentLabel(m.label) }
        }
    }
}

/**
 * Etiqueta de botón segmentado: sin ícono de check (más espacio), en una sola línea y con tamaño
 * que se reduce (14 → 9 sp) si no cabe. Así «Automático» o «Esteganografía clásica» nunca se parten
 * a media palabra y todos los segmentos tienen la misma altura, aun con la fuente del sistema agrandada.
 */
@Composable
private fun SegmentLabel(text: String) {
    BasicText(
        text = text,
        style = LocalTextStyle.current.copy(color = LocalContentColor.current, textAlign = TextAlign.Center),
        maxLines = 1,
        softWrap = false,
        overflow = TextOverflow.Ellipsis,
        autoSize = TextAutoSize.StepBased(minFontSize = 9.sp, maxFontSize = 14.sp, stepSize = 0.5.sp),
    )
}

/** Selector segmentado "Automático / Claro / Oscuro". */
@Composable
fun ThemeModeSelector(mode: ThemeMode, onChange: (ThemeMode) -> Unit, modifier: Modifier = Modifier) {
    val modes = ThemeMode.entries
    SingleChoiceSegmentedButtonRow(modifier.fillMaxWidth()) {
        modes.forEachIndexed { index, m ->
            SegmentedButton(
                selected = mode == m,
                onClick = { onChange(m) },
                shape = SegmentedButtonDefaults.itemShape(index = index, count = modes.size),
                icon = {},
            ) { SegmentLabel(m.label) }
        }
    }
}

/**
 * Vista previa de una imagen ajustada al ancho, con altura máxima. Mientras [bitmap] es null muestra un
 * indicador de progreso (la imagen se está decodificando).
 *
 * @param maxHeight altura máxima en dp.
 */
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

/** Fila "etiqueta: valor" para resultados. El valor se puede seleccionar y copiar. */
@Composable
fun InfoRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(
            label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(1f, fill = false),
        )
        Spacer(Modifier.width(12.dp))
        SelectionContainer(Modifier.weight(1f, fill = false)) {
            Text(
                value, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium,
                textAlign = TextAlign.End,
            )
        }
    }
}
