using System;
using UnityEngine;
using UnityEngine.UI;

namespace AvaTwin
{
public class AvaVariationButton : MonoBehaviour
{
    [SerializeField] private Button button;
    [SerializeField] private Image thumbnailImage, loadingImage;
 
    [SerializeField] private GameObject selectedIndicator;

    private string _variationId;

    public void Bind(AvatarVariation variation, Texture2D thumbnail, Action<string> onClick)
    {
        if (variation == null)
            return;

        _variationId = variation.variationId;

        SetThumbnail(thumbnail);

        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => onClick?.Invoke(_variationId));
        }
    }

    public string GetVariationId()
    {
        return _variationId;
    }

    public void SetThumbnail(Texture2D thumbnail)
    {
        if (thumbnailImage == null || thumbnail == null)
            return;

        var sprite = Sprite.Create(
            thumbnail,
            new Rect(0, 0, thumbnail.width, thumbnail.height),
            new Vector2(0.5f, 0.5f)
        );
        thumbnailImage.sprite = sprite;
        thumbnailImage.gameObject.SetActive(true);
        loadingImage.gameObject.SetActive(false);
    }

    public void SetSelected(bool isSelected)
    {
        if (selectedIndicator != null)
            selectedIndicator.SetActive(isSelected);
    }
}
}