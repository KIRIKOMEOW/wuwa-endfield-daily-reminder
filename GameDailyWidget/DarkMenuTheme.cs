using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace GameDailyWidget;

internal static class DarkMenuTheme
{
    public static void Apply(ContextMenu menu)
    {
        var resources = (ResourceDictionary)XamlReader.Parse(Theme);
        menu.Resources.MergedDictionaries.Add(resources);
        menu.Style = (Style)resources[typeof(ContextMenu)];
        ApplyItems(menu.Items, resources);
    }

    private static void ApplyItems(ItemCollection items, ResourceDictionary resources)
    {
        foreach (var item in items)
        {
            if (item is MenuItem menuItem)
            {
                menuItem.Style = (Style)resources[typeof(MenuItem)];
                if (menuItem.Header is DependencyObject header) ApplyContent(header, resources);
                ApplyItems(menuItem.Items, resources);
            }
            else if (item is Separator separator) separator.Style = (Style)resources[typeof(Separator)];
        }
    }

    private static void ApplyContent(DependencyObject content, ResourceDictionary resources)
    {
        if (content is Slider slider) slider.Style = (Style)resources[typeof(Slider)];
        foreach (var child in LogicalTreeHelper.GetChildren(content)) if (child is DependencyObject element) ApplyContent(element, resources);
    }

    private const string Theme = """
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style TargetType="ContextMenu">
        <Setter Property="OverridesDefaultStyle" Value="True" />
        <Setter Property="Background" Value="#2B2E36" />
        <Setter Property="Foreground" Value="#F2F0E9" />
        <Setter Property="BorderBrush" Value="#555A65" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="4" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="ContextMenu">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" MaxHeight="540"><ItemsPresenter /></ScrollViewer>
              </Border>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
      <Style TargetType="MenuItem">
        <Setter Property="OverridesDefaultStyle" Value="True" />
        <Setter Property="Foreground" Value="#F2F0E9" />
        <Setter Property="Background" Value="#2B2E36" />
        <Setter Property="BorderBrush" Value="Transparent" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="7,5" />
        <Setter Property="MinHeight" Value="29" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="MenuItem">
              <Grid>
                <Border x:Name="ItemBorder" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" Padding="{TemplateBinding Padding}">
                  <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="19" /><ColumnDefinition Width="*" MinWidth="116" /><ColumnDefinition Width="Auto" /><ColumnDefinition Width="15" /></Grid.ColumnDefinitions>
                    <ContentPresenter x:Name="IconPresenter" ContentSource="Icon" VerticalAlignment="Center" HorizontalAlignment="Center" />
                    <Path x:Name="CheckMark" Visibility="Collapsed" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.8" Data="M 1,5 L 4,8 L 10,1" HorizontalAlignment="Center" VerticalAlignment="Center" />
                    <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center" />
                    <TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}" Foreground="#AAAEB8" Margin="14,0,5,0" VerticalAlignment="Center" FontSize="10" />
                    <Path x:Name="SubmenuArrow" Grid.Column="3" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5" Data="M 1,1 L 5,5 L 1,9" VerticalAlignment="Center" HorizontalAlignment="Center" />
                  </Grid>
                </Border>
                <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsSubmenuOpen}" Placement="Right" PlacementTarget="{Binding RelativeSource={RelativeSource TemplatedParent}}" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade" HorizontalOffset="2">
                  <Border x:Name="SubmenuBorder" Background="#2B2E36" BorderBrush="#555A65" BorderThickness="1" CornerRadius="7" Padding="4" TextElement.Foreground="#F2F0E9">
                    <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" MaxHeight="520"><ItemsPresenter /></ScrollViewer>
                  </Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="Role" Value="TopLevelHeader"><Setter TargetName="PART_Popup" Property="Placement" Value="Bottom" /><Setter TargetName="PART_Popup" Property="HorizontalOffset" Value="0" /><Setter TargetName="SubmenuBorder" Property="Background" Value="#2B2E36" /></Trigger>
                <Trigger Property="Role" Value="SubmenuHeader"><Setter TargetName="PART_Popup" Property="Placement" Value="Right" /><Setter TargetName="SubmenuBorder" Property="Background" Value="#2B2E36" /></Trigger>
                <Trigger Property="Role" Value="TopLevelItem"><Setter TargetName="SubmenuArrow" Property="Visibility" Value="Collapsed" /></Trigger>
                <Trigger Property="Role" Value="SubmenuItem"><Setter TargetName="SubmenuArrow" Property="Visibility" Value="Collapsed" /></Trigger>
                <Trigger Property="HasItems" Value="False"><Setter TargetName="SubmenuArrow" Property="Visibility" Value="Collapsed" /></Trigger>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemBorder" Property="Background" Value="#424751" /><Setter TargetName="ItemBorder" Property="BorderBrush" Value="#555D69" /></Trigger>
                <Trigger Property="IsSubmenuOpen" Value="True"><Setter TargetName="ItemBorder" Property="Background" Value="#3B414B" /><Setter TargetName="ItemBorder" Property="BorderBrush" Value="#626A78" /></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="CheckMark" Property="Visibility" Value="Visible" /><Setter TargetName="IconPresenter" Property="Visibility" Value="Hidden" /></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#777D89" /><Setter TargetName="ItemBorder" Property="Background" Value="#2B2E36" /><Setter TargetName="ItemBorder" Property="BorderBrush" Value="Transparent" /></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
      <Style TargetType="Separator">
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Background="#484E59" Margin="22,4,8,4" /></ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="Slider">
        <Setter Property="OverridesDefaultStyle" Value="True" />
        <Setter Property="Height" Value="24" />
        <Setter Property="Foreground" Value="#D6BB87" />
        <Setter Property="Background" Value="#4A505B" />
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="Slider">
              <Grid Background="Transparent" Height="24">
                <Border Height="4" CornerRadius="2" Background="{TemplateBinding Background}" VerticalAlignment="Center" Margin="5,0" />
                <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                  <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" CornerRadius="2" Background="#D6BB87" VerticalAlignment="Center" /></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                  <Track.Thumb><Thumb Width="12" Height="16"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="Grip" Background="#D5C8AA" BorderBrush="#F1EBD9" BorderThickness="1" CornerRadius="3" /><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Background" Value="#EFE2C6" /></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Grip" Property="Background" Value="#F9EBCB" /></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Grip" Property="Background" Value="#777D89" /><Setter TargetName="Grip" Property="BorderBrush" Value="#8C929D" /></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" Background="Transparent" VerticalAlignment="Center" /></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                </Track>
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.55" /></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
      <Style TargetType="ToolTip">
        <Setter Property="Foreground" Value="#F2F0E9" /><Setter Property="Background" Value="#2B2E36" /><Setter Property="BorderBrush" Value="#555A65" />
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToolTip"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" Padding="7,5" CornerRadius="4"><ContentPresenter /></Border></ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="ScrollBar">
        <Setter Property="OverridesDefaultStyle" Value="True" /><Setter Property="Width" Value="8" /><Setter Property="Background" Value="#2B2E36" />
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar"><Grid Background="{TemplateBinding Background}"><Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" ViewportSize="{TemplateBinding ViewportSize}" Value="{Binding Value, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"><Track.DecreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton><Track.Thumb><Thumb MinHeight="20" Margin="2,0"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="#68717E" CornerRadius="2" /></ControlTemplate></Thumb.Template></Thumb></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton></Track></Grid></ControlTemplate></Setter.Value></Setter>
      </Style>
    </ResourceDictionary>
    """;
}
